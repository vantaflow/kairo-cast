using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using KairoCast.App.Diagnostics;
using KairoCast.Media;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace KairoCast.App.Capture;

/// <summary>
/// Encodeur H.264 Media Foundation : MFT matériel (asynchrone, textures D3D11) de préférence,
/// sinon encodeur logiciel Microsoft (synchrone, NV12 en mémoire). Faible latence, CBR, sans images B,
/// GOP long, IDR à la demande. Sortie Annex-B avec SPS/PPS devant chaque IDR.
/// Tout le pilotage du MFT se fait sur un fil dédié ; un second fil lit les événements des MFT asynchrones.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    /// <summary>Unité d'accès prête : octets Annex-B, image clé, date µs. Renvoie false si la session est morte.</summary>
    public delegate bool AccessUnitSink(byte[] au, bool key, long ptsUs);

    private enum WorkKind { Frame, KeyRequest, NeedInput, HaveOutput, DrainComplete, EventError }

    private readonly record struct Work(WorkKind Kind, int Status = 0, string? Detail = null);

    private const long IdleRepeatUs = 250_000;
    private const long StatsIntervalUs = 5_000_000;

    private readonly GpuDevice _gpu;
    private readonly VideoConverter _conv;
    private readonly FrameSlots _slots;
    private readonly StreamOptions _opt;
    private readonly MediaClock _clock;
    private readonly AccessUnitSink _sink;
    private readonly BlockingCollection<Work> _work = new();
    private readonly ParameterSetCache _paramSets = new();
    private readonly Queue<(int slot, long pts)> _inFlight = new();

    private IMFActivateCollection? _activates;
    private IMFActivate? _activate;
    private IMFTransform? _mft;
    private IMFMediaEventGenerator? _events;
    private IMFDXGIDeviceManager? _manager;
    private CodecApi? _codec;
    private ID3D11Texture2D? _staging;
    private bool _async;
    private bool _providesSamples;
    private int _outputBufferSize;

    private Thread? _thread;
    private Thread? _eventThread;
    private volatile bool _stopping;
    private int _keyRequested;
    private int _needInput;
    private long _lastSubmitUs = long.MinValue;
    private long _lastPts = long.MinValue;
    private long _keyRequestedAtUs = -1;
    private int _keyRetries;
    private bool _warnedAvcc, _warnedNoParamSets, _warnedInFlight;
    private bool _firstOutputLogged;

    // Statistiques (fil de l'encodeur).
    private long _statIn, _statOut, _statKeys, _statBytes, _statRepeats, _statLatSum, _statLatMax, _statStart;

    public string Name { get; private set; } = "?";
    public bool IsHardware { get; private set; }

    /// <summary>Erreur fatale de l'encodeur (levée sur le fil de l'encodeur).</summary>
    public event Action<Exception>? Failed;

    private H264Encoder(GpuDevice gpu, VideoConverter conv, FrameSlots slots, StreamOptions opt, MediaClock clock, AccessUnitSink sink)
    {
        _gpu = gpu;
        _conv = conv;
        _slots = slots;
        _opt = opt;
        _clock = clock;
        _sink = sink;
    }

    /// <summary>Crée et configure l'encodeur : essaie chaque MFT matériel, puis le logiciel.</summary>
    public static H264Encoder Create(GpuDevice gpu, VideoConverter conv, FrameSlots slots, StreamOptions opt, MediaClock clock, AccessUnitSink sink)
    {
        var enc = new H264Encoder(gpu, conv, slots, opt, clock, sink);
        try
        {
            enc.Init();
            return enc;
        }
        catch
        {
            enc.Dispose();
            throw;
        }
    }

    private void Init()
    {
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var nv12 = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var errors = new List<string>();

        // 1) Matériels, triés par Media Foundation (mérite), en préférant le GPU de la capture.
        IMFActivateCollection? hw = null;
        try { hw = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), null, output); }
        catch (Exception e) { Log.Warn($"Encodeur : énumération des MFT matériels impossible : {Log.Describe(e)}"); }
        if (hw != null)
        {
            var list = hw.ToList();
            Log.Info($"Encodeur : {list.Count} MFT H.264 matériel(s)");
            foreach (var a in list) Log.Info($"  - {Describe(a)}");
            var ordered = list.OrderByDescending(a => Luid(a) == _gpu.AdapterLuid).ToList();
            foreach (var a in ordered)
            {
                if (TryConfigure(a, hardware: true, errors)) { _activates = hw; return; }
            }
            hw.Dispose();
        }

        // 2) Logiciel (encodeur H.264 Microsoft, synchrone).
        IMFActivateCollection? sw = null;
        try { sw = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter), nv12, output); }
        catch (Exception e) { Log.Warn($"Encodeur : énumération des MFT logiciels impossible : {Log.Describe(e)}"); }
        if (sw != null)
        {
            var list = sw.ToList();
            Log.Info($"Encodeur : {list.Count} MFT H.264 logiciel(s)");
            foreach (var a in list) Log.Info($"  - {Describe(a)}");
            foreach (var a in list)
            {
                if (TryConfigure(a, hardware: false, errors)) { _activates = sw; return; }
            }
            sw.Dispose();
        }
        throw new InvalidOperationException("aucun encodeur H.264 utilisable : " + string.Join(" ; ", errors));
    }

    private static string FriendlyName(IMFActivate a)
    {
        try { return a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { return "(sans nom)"; }
    }

    private static long Luid(IMFActivate a)
    {
        try { return (long)a.GetUInt64(CodecGuids.MftEnumAdapterLuid); } catch { return 0; }
    }

    private static string Describe(IMFActivate a)
    {
        uint flags = 0;
        try { flags = a.GetUInt32(TransformAttributeKeys.TransformFlagsAttribute); } catch { }
        string url = "";
        try { url = a.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute); } catch { }
        return $"« {FriendlyName(a)} » flags=0x{flags:X} LUID=0x{Luid(a):X}{(url.Length > 0 ? " url=" + url : "")}";
    }

    private bool TryConfigure(IMFActivate activate, bool hardware, List<string> errors)
    {
        var name = FriendlyName(activate);
        try
        {
            Configure(activate, hardware);
            _activate = activate;
            Name = name;
            IsHardware = hardware;
            Log.Info($"Encodeur choisi : « {name} » ({(hardware ? "matériel" : "logiciel")}, {(_async ? "asynchrone" : "synchrone")}, " +
                     $"{_opt.Width}x{_opt.Height}@{_opt.Fps} {_opt.BitrateKbps} kb/s CBR, Main, GOP {Gop}, sans B, " +
                     $"échantillons de sortie {(_providesSamples ? "fournis par le MFT" : $"alloués ({_outputBufferSize} o)")})");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Encodeur « {name} » inutilisable", e);
            errors.Add($"{name}: {Log.Hr(e)}");
            ShutdownTransform();
            ReleaseTransform(activate);
            return false;
        }
    }

    private uint Gop => (uint)Math.Max(_opt.Fps * 10, 30);

    private void Configure(IMFActivate activate, bool hardware)
    {
        _mft = activate.ActivateObject<IMFTransform>();
        IMFAttributes? attrs = null;
        try { attrs = _mft.Attributes; }
        catch (Exception e) { Log.Info($"  attributs du MFT indisponibles ({Log.Hr(e)})"); }

        _async = false;
        var d3dAware = false;
        if (attrs != null)
        {
            _async = TryGetUInt32(attrs, TransformAttributeKeys.TransformAsync) == 1;
            d3dAware = TryGetUInt32(attrs, TransformAttributeKeys.D3D11Aware) == 1;
            if (_async) attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            var hr = attrs.Set(CodecGuids.MFLowLatency, 1u);
            if (hr.Failure) Log.Info($"  MF_LOW_LATENCY refusé (hr=0x{hr.Code:X8})");
        }
        Log.Info($"  MFT : asynchrone={_async}, D3D11={d3dAware}");

        if (hardware)
        {
            if (!d3dAware) throw new NotSupportedException("MFT matériel sans prise en charge D3D11");
            if (!_async) Log.Warn("  MFT matériel synchrone : inhabituel, on tente quand même");
            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(_gpu.Device).CheckError();
            _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_manager.NativePointer);
            Log.Info("  gestionnaire DXGI attaché au MFT");
        }

        _codec = CodecApi.From(_mft.NativePointer, out var qhr);
        if (_codec == null) Log.Warn($"  ICodecAPI indisponible ({Log.Hr(qhr)}) : réglages par type de média seulement");
        var pendingCodec = ApplyCodecSettings(firstPass: null);

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(_opt.BitrateKbps * 1000)).CheckError();
            SetVideoGeometry(outType);
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, CodecGuids.ProfileMain).CheckError();
            _mft.SetOutputType(0, outType, 0);
        }
        Log.Info($"  type de sortie H.264 {_opt.Width}x{_opt.Height}@{_opt.Fps} accepté");

        SetInputType();

        // Les réglages refusés avant les types sont retentés une fois les types posés.
        if (pendingCodec.Count > 0) ApplyCodecSettings(pendingCodec);

        try
        {
            using var cur = _mft.GetOutputCurrentType(0);
            var header = cur.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (header is { Length: > 0 })
            {
                _paramSets.LoadSequenceHeader(header);
                Log.Info($"  en-tête de séquence : {header.Length} o, NAL {AnnexB.Describe(header)}");
            }
        }
        catch (Exception e) { Log.Info($"  pas d'en-tête de séquence dans le type de sortie ({Log.Hr(e)}) : SPS/PPS pris dans le flux"); }

        var info = _mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outputBufferSize = Math.Max(info.Size, _opt.Width * _opt.Height * 3 / 2);
        Log.Info($"  flux de sortie : flags=0x{info.Flags:X} taille={info.Size} alignement={info.Alignment}");

        if (!hardware)
        {
            _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_opt.Width,
                Height = (uint)_opt.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        if (_async) _events = _mft.QueryInterface<IMFMediaEventGenerator>();
    }

    private void SetVideoGeometry(IMFMediaType t)
    {
        MediaFactory.MFSetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, (uint)_opt.Width, (uint)_opt.Height).CheckError();
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.FrameRate, (uint)_opt.Fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        t.Set(MediaTypeAttributeKeys.InterlaceMode, CodecGuids.InterlaceProgressive).CheckError();
    }

    private void SetInputType()
    {
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            SetVideoGeometry(inType);
            try
            {
                _mft!.SetInputType(0, inType, 0);
                Log.Info("  type d'entrée NV12 accepté");
                return;
            }
            catch (SharpGenException e)
            {
                Log.Info($"  type d'entrée NV12 direct refusé ({Log.Hr(e)}), recherche parmi les types proposés");
            }
        }
        for (var i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft!.GetInputAvailableType(0, i); }
            catch (SharpGenException e) { throw new NotSupportedException($"aucun type d'entrée NV12 accepté ({Log.Hr(e)})", e); }
            using (t)
            {
                Guid sub;
                try { sub = t.GetGUID(MediaTypeAttributeKeys.Subtype); } catch { continue; }
                if (sub != VideoFormatGuids.NV12) continue;
                SetVideoGeometry(t);
                _mft!.SetInputType(0, t, 0);
                Log.Info($"  type d'entrée NV12 accepté (type proposé n°{i})");
                return;
            }
        }
    }

    /// <summary>Applique les réglages ICodecAPI ; renvoie ceux refusés (à retenter après les types).</summary>
    private List<(string name, Guid key, uint value, bool isBool)> ApplyCodecSettings(List<(string name, Guid key, uint value, bool isBool)>? firstPass)
    {
        var settings = firstPass ?? new List<(string, Guid, uint, bool)>
        {
            ("AVLowLatencyMode", CodecGuids.AVLowLatencyMode, 1, true),
            ("AVEncCommonRateControlMode=CBR", CodecGuids.AVEncCommonRateControlMode, CodecGuids.RateControlCbr, false),
            ("AVEncCommonMeanBitRate", CodecGuids.AVEncCommonMeanBitRate, (uint)(_opt.BitrateKbps * 1000), false),
            ("AVEncMPVDefaultBPictureCount=0", CodecGuids.AVEncMPVDefaultBPictureCount, 0, false),
            ("AVEncMPVGOPSize", CodecGuids.AVEncMPVGOPSize, Gop, false),
        };
        var refused = new List<(string, Guid, uint, bool)>();
        if (_codec == null) return refused;
        foreach (var s in settings)
        {
            var hr = s.isBool ? _codec.SetBool(s.key, s.value != 0) : _codec.SetUInt32(s.key, s.value);
            var pass = firstPass == null ? "avant types" : "après types";
            if (hr < 0)
            {
                Log.Info($"  ICodecAPI {s.name} ({s.value}) refusé {pass} : {Log.Hr(hr)} (IsSupported={Log.Hr(_codec.IsSupported(s.key))})");
                refused.Add(s);
            }
            else Log.Info($"  ICodecAPI {s.name} ({s.value}) OK {pass}");
        }
        if (firstPass != null && refused.Count > 0)
            Log.Warn($"  réglages refusés définitivement : {string.Join(", ", refused.Select(r => r.Item1))}");
        return refused;
    }

    private static uint TryGetUInt32(IMFAttributes a, Guid key)
    {
        try { return a.GetUInt32(key); } catch { return 0; }
    }

    // ------------------------------------------------------------------ fonctionnement

    public void Start()
    {
        _statStart = _clock.NowUs;
        _thread = new Thread(Run) { IsBackground = true, Name = "KairoCast encodeur", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (_async)
        {
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "KairoCast événements MFT" };
            _eventThread.Start();
        }
    }

    /// <summary>Une image capturée vient d'être publiée dans <see cref="FrameSlots"/>.</summary>
    public void NotifyFrame()
    {
        if (!_stopping) TryAdd(new Work(WorkKind.Frame));
    }

    /// <summary>La box demande une image clé (KEYREQ) : la prochaine image envoyée sera une IDR.</summary>
    public void RequestKeyFrame()
    {
        if (_stopping) return;
        Interlocked.Exchange(ref _keyRequested, 1);
        TryAdd(new Work(WorkKind.KeyRequest));
    }

    private void TryAdd(Work w)
    {
        try { _work.Add(w); } catch (InvalidOperationException) { /* fermé (ObjectDisposedException en dérive) */ }
    }

    private void EventLoop()
    {
        try
        {
            while (!_stopping)
            {
                IMFMediaEvent ev;
                try { ev = _events!.GetEvent(0); }
                catch (SharpGenException e) when (_stopping || e.ResultCode == ResultCode.Shutdown) { return; }
                using (ev)
                {
                    var type = ev.EventType;
                    var status = ev.Status.Code;
                    switch (type)
                    {
                        case MediaEventTypes.TransformNeedInput: TryAdd(new Work(WorkKind.NeedInput)); break;
                        case MediaEventTypes.TransformHaveOutput: TryAdd(new Work(WorkKind.HaveOutput)); break;
                        case MediaEventTypes.TransformDrainComplete: TryAdd(new Work(WorkKind.DrainComplete)); break;
                        case MediaEventTypes.Error:
                            TryAdd(new Work(WorkKind.EventError, status, "MEError"));
                            break;
                        default:
                            Log.Info($"Encodeur : événement {type} ({(int)type}) statut {Log.Hr(status)}");
                            break;
                    }
                    if (status < 0 && type != MediaEventTypes.Error)
                        TryAdd(new Work(WorkKind.EventError, status, type.ToString()));
                }
            }
        }
        catch (Exception e)
        {
            if (!_stopping) TryAdd(new Work(WorkKind.EventError, e.HResult, Log.Describe(e)));
        }
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                if (_work.TryTake(out var w, 20))
                {
                    switch (w.Kind)
                    {
                        case WorkKind.NeedInput: _needInput++; break;
                        case WorkKind.HaveOutput: DrainOne(); break;
                        case WorkKind.EventError:
                            throw new COMException($"l'encodeur signale une erreur ({w.Detail})", w.Status);
                    }
                }
                if (_stopping) break;
                Feed();
                MaybeLogStats();
            }
        }
        catch (Exception e)
        {
            if (_stopping) return;
            Log.Error("Encodeur : arrêt sur erreur", e);
            try { Failed?.Invoke(e); } catch { }
        }
    }

    private void Feed()
    {
        while (!_stopping && (!_async || _needInput > 0) && TryGetInput(out var slot, out var pts, out var repeat))
        {
            Submit(slot, pts, repeat);
            if (_async) _needInput--;
            else while (DrainOne()) { }
        }
        if (Volatile.Read(ref _keyRequested) == 0 && _keyRequestedAtUs >= 0 && _clock.NowUs - _keyRequestedAtUs > 2_000_000)
        {
            _keyRetries++;
            _keyRequestedAtUs = -1;
            if (_keyRetries <= 3)
            {
                Log.Warn($"Encodeur : IDR demandée il y a plus de 2 s et toujours pas reçue, nouvelle demande ({_keyRetries}/3)");
                Interlocked.Exchange(ref _keyRequested, 1);
            }
            else Log.Error("Encodeur : l'encodeur ignore AVEncVideoForceKeyFrame ; la box attendra l'IDR périodique (GOP)");
        }
    }

    private bool TryGetInput(out int slot, out long pts, out bool repeat)
    {
        repeat = false;
        if (_slots.TryTakePending(out slot, out pts)) return true;
        var now = _clock.NowUs;
        // Écran immobile : la capture ne livre plus rien. On répète la dernière image pour sortir l'image
        // retenue dans l'encodeur, et pour servir une demande d'image clé.
        var wantKey = Volatile.Read(ref _keyRequested) == 1;
        if ((wantKey || (_lastSubmitUs != long.MinValue && now - _lastSubmitUs >= IdleRepeatUs)) && _slots.TryTakeLastForRepeat(out slot))
        {
            pts = now;
            repeat = true;
            return true;
        }
        return false;
    }

    private void Submit(int slot, long pts, bool repeat)
    {
        if (_lastPts != long.MinValue && pts <= _lastPts) pts = _lastPts + 1000;
        _lastPts = pts;
        _lastSubmitUs = _clock.NowUs;

        if (Interlocked.Exchange(ref _keyRequested, 0) == 1)
        {
            var hr = _codec?.SetUInt32(CodecGuids.AVEncVideoForceKeyFrame, 1) ?? unchecked((int)0x80004001);
            if (hr < 0) Log.Warn($"Encodeur : AVEncVideoForceKeyFrame refusé ({Log.Hr(hr)})");
            else Log.Info($"Encodeur : IDR forcée sur l'image {pts} µs{(repeat ? " (répétée)" : "")}");
            if (_keyRequestedAtUs < 0) _keyRequestedAtUs = _lastSubmitUs;
        }

        IMFSample? sample = null;
        var handedToEncoder = false;
        try
        {
            sample = IsHardware ? GpuSample(slot) : CpuSample(slot);
            if (!IsHardware) { _slots.Release(slot); handedToEncoder = true; }
            sample.SampleTime = MediaClock.ToHns(pts);
            sample.SampleDuration = 10_000_000L / _opt.Fps;
            try
            {
                _mft!.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException e) when (e.ResultCode == ResultCode.Notaccepting && !_async)
            {
                while (DrainOne()) { }
                _mft!.ProcessInput(0, sample, 0);
            }
            if (IsHardware)
            {
                _inFlight.Enqueue((slot, pts));
                handedToEncoder = true;
                // Garde-fou : un MFT qui ne rend pas les images bloquerait l'anneau.
                while (_inFlight.Count > Math.Max(1, _slots.Count - 2))
                {
                    if (!_warnedInFlight) { _warnedInFlight = true; Log.Warn($"Encodeur : {_inFlight.Count} images en cours, libération anticipée de la plus ancienne"); }
                    _slots.Release(_inFlight.Dequeue().slot);
                }
            }
            _statIn++;
            if (repeat) _statRepeats++;
        }
        finally
        {
            if (!handedToEncoder) _slots.Release(slot);
            sample?.Dispose();
        }
    }

    private IMFSample GpuSample(int slot)
    {
        var sample = MediaFactory.MFCreateSample();
        try
        {
            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, _conv.Outputs[slot], 0, false);
            try
            {
                using var b2 = buffer.QueryInterface<IMF2DBuffer>();
                buffer.CurrentLength = b2.ContiguousLength;
            }
            catch { /* certains tampons DXGI n'exposent pas IMF2DBuffer : sans gravité */ }
            sample.AddBuffer(buffer);
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private unsafe IMFSample CpuSample(int slot)
    {
        var w = _opt.Width;
        var h = _opt.Height;
        var size = w * h * 3 / 2;
        var ctx = _gpu.Context;
        ctx.CopyResource(_staging!, _conv.Outputs[slot]);
        var sample = MediaFactory.MFCreateSample();
        try
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
            buffer.Lock(out var dst, out _, out _);
            try
            {
                var map = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var pitch = (int)map.RowPitch;
                    var src = (byte*)map.DataPointer;
                    var d = (byte*)dst;
                    for (var y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + (long)y * pitch, d + (long)y * w, w, w);
                    // NV12 : plan UV juste après les h lignes du plan Y, même pas de ligne.
                    var uvSrc = src + (long)pitch * h;
                    var uvDst = d + (long)w * h;
                    for (var y = 0; y < h / 2; y++)
                        Buffer.MemoryCopy(uvSrc + (long)y * pitch, uvDst + (long)y * w, w, w);
                }
                finally { ctx.Unmap(_staging!, 0); }
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = size;
            sample.AddBuffer(buffer);
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    /// <summary>Un appel à ProcessOutput. Renvoie true si une unité d'accès a été produite.</summary>
    private bool DrainOne()
    {
        IMFSample? own = null;
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            using var buf = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            own.AddBuffer(buf);
        }
        var odb = new OutputDataBuffer { StreamID = 0, Sample = own };
        Result hr;
        try
        {
            hr = _mft!.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
        }
        catch
        {
            own?.Dispose();
            throw;
        }
        // Vortice enveloppe à nouveau le pointeur renvoyé : ne libérer qu'une seule enveloppe.
        var outSample = own ?? odb.Sample;
        try
        {
            odb.Events?.Dispose();
            if (hr == ResultCode.TransformNeedMoreInput) return false;
            if (hr == ResultCode.TransformStreamChange)
            {
                Log.Info("Encodeur : changement de flux annoncé, renégociation du type de sortie");
                using var t = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, t, 0);
                try
                {
                    var header = t.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
                    if (header is { Length: > 0 }) _paramSets.LoadSequenceHeader(header);
                }
                catch { }
                var info = _mft.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
                _outputBufferSize = Math.Max(info.Size, _outputBufferSize);
                return !_async;
            }
            hr.CheckError();
            if (outSample == null) return true;
            Deliver(outSample);
            return true;
        }
        finally
        {
            outSample?.Dispose();
        }
    }

    private void Deliver(IMFSample sample)
    {
        byte[] data;
        using (var buf = sample.ConvertToContiguousBuffer())
        {
            buf.Lock(out var p, out _, out var len);
            try
            {
                data = new byte[len];
                Marshal.Copy(p, data, 0, len);
            }
            finally { buf.Unlock(); }
        }

        long pts;
        try { pts = MediaClock.FromHns(sample.SampleTime); }
        catch { pts = _inFlight.Count > 0 ? _inFlight.Peek().pts : _clock.NowUs; }
        if (IsHardware && _inFlight.Count > 0) _slots.Release(_inFlight.Dequeue().slot);
        if (data.Length == 0) return;

        if (!AnnexB.HasStartCode(data))
        {
            var converted = AnnexB.FromLengthPrefixed(data);
            if (!_warnedAvcc) { _warnedAvcc = true; Log.Warn($"Encodeur : sortie sans code de début (AVCC ?), conversion {(converted != null ? "réussie" : "impossible")}"); }
            if (converted == null) return;
            data = converted;
        }

        var au = _paramSets.Prepare(data, out var key);
        if (!_firstOutputLogged)
        {
            _firstOutputLogged = true;
            Log.Info($"Encodeur : première unité d'accès {data.Length} o, NAL [{AnnexB.Describe(data)}], envoyée [{AnnexB.Describe(au)}], clé={key}");
        }
        if (key)
        {
            _statKeys++;
            if (!_paramSets.HasParameterSets && !_warnedNoParamSets)
            {
                _warnedNoParamSets = true;
                Log.Error("Encodeur : IDR sans SPS/PPS et aucun en-tête en cache : la box ne pourra pas décoder");
            }
            if (_keyRequestedAtUs >= 0)
            {
                Log.Info($"Encodeur : IDR livrée {(_clock.NowUs - _keyRequestedAtUs) / 1000} ms après la demande ({au.Length} o, NAL [{AnnexB.Describe(au)}])");
                _keyRequestedAtUs = -1;
                _keyRetries = 0;
            }
        }
        _statOut++;
        _statBytes += au.Length;
        var lat = _clock.NowUs - pts;
        if (lat >= 0)
        {
            _statLatSum += lat;
            if (lat > _statLatMax) _statLatMax = lat;
        }
        if (!_sink(au, key, pts)) _stopping = true;
    }

    private void MaybeLogStats()
    {
        var now = _clock.NowUs;
        var elapsed = now - _statStart;
        if (elapsed < StatsIntervalUs) return;
        var secs = elapsed / 1e6;
        Log.Info($"Encodeur : {_statIn / secs:F1} i/s en entrée ({_statRepeats} répétées), {_statOut / secs:F1} i/s en sortie, " +
                 $"{_statBytes * 8 / secs / 1000:F0} kb/s, {_statKeys} IDR, capture→encodé moyen {(_statOut > 0 ? _statLatSum / _statOut / 1000 : 0)} ms " +
                 $"max {_statLatMax / 1000} ms, images remplacées {_slots.Replaced}, en cours {_inFlight.Count}");
        _statIn = _statOut = _statKeys = _statBytes = _statRepeats = _statLatSum = _statLatMax = 0;
        _statStart = now;
    }

    // ------------------------------------------------------------------ arrêt

    /// <summary>Demande l'arrêt et attend le fil de l'encodeur. Renvoie false s'il ne s'est pas arrêté à temps (envoi bloqué).</summary>
    public bool Stop(TimeSpan timeout)
    {
        _stopping = true;
        TryAdd(new Work(WorkKind.Frame)); // réveil
        var ok = _thread == null || _thread.Join(timeout);
        if (!ok) Log.Warn("Encodeur : le fil ne s'est pas arrêté à temps");
        return ok;
    }

    /// <summary>Fin de flux, vidage puis IMFShutdown (débloque GetEvent sur le fil d'événements).</summary>
    private void ShutdownTransform()
    {
        if (_mft == null) return;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try
        {
            using var shutdown = _mft.QueryInterface<IMFShutdown>();
            shutdown.Shutdown();
        }
        catch { /* les MFT synchrones n'ont pas IMFShutdown */ }
    }

    private void ReleaseTransform(IMFActivate? activate)
    {
        try { _events?.Dispose(); } catch { }
        _events = null;
        try { _codec?.Dispose(); } catch { }
        _codec = null;
        try { _mft?.Dispose(); } catch { }
        _mft = null;
        try { activate?.ShutdownObject(); } catch { }
        try { _manager?.Dispose(); } catch { }
        _manager = null;
        try { _staging?.Dispose(); } catch { }
        _staging = null;
    }

    public void Dispose()
    {
        var joined = Stop(TimeSpan.FromSeconds(3));
        if (!joined)
        {
            // Ne jamais libérer le MFT sous un fil qui s'en sert encore.
            Log.Error("Encodeur : fil toujours actif, MFT non libéré (fuite volontaire)");
            return;
        }
        ShutdownTransform();
        if (_eventThread != null && !_eventThread.Join(TimeSpan.FromSeconds(2)))
            Log.Warn("Encodeur : le fil d'événements ne s'est pas arrêté");
        while (_inFlight.Count > 0) { try { _slots.Release(_inFlight.Dequeue().slot); } catch { } }
        ReleaseTransform(_activate);
        try { _activates?.Dispose(); } catch { }
        _activates = null;
        _work.CompleteAdding();
        Log.Info($"Encodeur « {Name} » libéré");
    }
}
