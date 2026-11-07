using System.Net;
using KairoCast.App.Capture;
using KairoCast.App.Diagnostics;
using KairoCast.Link;
using KairoCast.Media;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace KairoCast.App.Streaming;

/// <summary>
/// Une session de partage vers une box : capture d'écran → processeur vidéo (NV12) → encodeur H.264 → Kairo Link,
/// et son WASAPI loopback → Kairo Link. Une seule horloge en µs pour l'audio et la vidéo.
/// Une coupure de la liaison, une erreur de l'encodeur ou la fermeture de l'écran arrêtent tout.
/// </summary>
public sealed class StreamSession : IAsyncDisposable
{
    public const string AudioFormat = "pcm_s16le_48000_2";
    // Les MFT matériels asynchrones (QSV, NVENC) gardent 3 à 4 images en vol : 8 textures laissent de la marge.
    private const int SlotCount = 8;

    private readonly StreamOptions _opt;
    private readonly BoxInfo _box;
    private readonly MediaClock _clock = new();
    private readonly FrameSlots _slots = new(SlotCount);
    private readonly FramePacer _pacer;
    private readonly object _statusLock = new();
    private LinkClient? _link;
    private GpuDevice? _gpu;
    private VideoConverter? _conv;
    private H264Encoder? _encoder;
    private ScreenCapture? _capture;
    private AudioLoopback? _audio;
    private System.Threading.Timer? _statsTimer;
    private bool _mfStarted;
    private volatile bool _stopping;
    private Task? _stopTask;
    private string _status = "Démarrage…";
    private long _captured, _paced, _noSlot, _convertErrors, _videoSent, _videoBytes;

    /// <summary>Changement d'état, en français, prêt à afficher.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>La session s'est arrêtée d'elle-même (coupure, erreur) ou par <see cref="DisposeAsync"/>. null = arrêt normal.</summary>
    public event Action<Exception?>? Ended;

    public bool UsingHardwareEncoder { get; private set; }
    public string EncoderName { get; private set; } = "";
    public bool AudioActive { get; private set; }
    public StreamOptions Options => _opt;
    public BoxInfo Box => _box;

    public string Status
    {
        get { lock (_statusLock) return _status; }
    }

    private StreamSession(BoxInfo box, StreamOptions opt)
    {
        _box = box;
        _opt = opt;
        _pacer = new FramePacer(opt.Fps);
    }

    public static Task<StreamSession> StartAsync(BoxInfo box, StreamOptions opt, CancellationToken ct,
        Action<Exception?>? onEnded = null, Action<string>? onStatus = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(opt);
        // Initialisation COM/Media Foundation sur un fil MTA du pool, jamais sur le fil de l'interface.
        return Task.Run(() => StartCoreAsync(box, opt.Validated(), ct, onEnded, onStatus), ct);
    }

    private static async Task<StreamSession> StartCoreAsync(BoxInfo box, StreamOptions opt, CancellationToken ct,
        Action<Exception?>? onEnded, Action<string>? onStatus)
    {
        var s = new StreamSession(box, opt);
        // Abonnement avant l'initialisation : aucun événement ne peut être manqué.
        if (onEnded != null) s.Ended += onEnded;
        if (onStatus != null) s.StatusChanged += onStatus;
        Log.Info($"=== Session vers « {box.Name} » {box.Address}:{box.Port} : {opt.Width}x{opt.Height}@{opt.Fps}, {opt.BitrateKbps} kb/s, son={opt.Audio} ; " +
                 $"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} cœurs, .NET {Environment.Version}, PC « {Environment.MachineName} »");
        try
        {
            await s.InitAsync(ct);
            return s;
        }
        catch (Exception e)
        {
            Log.Error("Session : démarrage impossible", e);
            await s.StopAsync(e, raiseEnded: false);
            throw;
        }
    }

    private async Task InitAsync(CancellationToken ct)
    {
        MediaFactory.MFStartup(false).CheckError();
        _mfStarted = true;
        _gpu = GpuDevice.Create();
        _conv = new VideoConverter(_gpu, _opt.Width, _opt.Height, _opt.Fps, SlotCount);
        _encoder = H264Encoder.Create(_gpu, _conv, _slots, _opt, _clock, SendVideo);
        UsingHardwareEncoder = _encoder.IsHardware;
        EncoderName = _encoder.Name;
        _encoder.Failed += e => _ = StopAsync(e, raiseEnded: true, status: "Erreur de l'encodeur vidéo : partage arrêté");
        ct.ThrowIfCancellationRequested();

        _capture = new ScreenCapture(_gpu, _clock);
        _capture.FrameArrived += OnFrame;
        _capture.Closed += () => _ = StopAsync(null, raiseEnded: true, status: "L'écran capturé a disparu : partage arrêté");

        SetStatus($"Connexion à {_box.Name}…");
        _link = new LinkClient();
        _link.KeyFrameRequested += () =>
        {
            Log.Info("Liaison : la box demande une image clé (KEYREQ)");
            _encoder?.RequestKeyFrame();
        };
        _link.Disconnected += e =>
        {
            if (_stopping) return;
            if (e != null) Log.Error("Liaison : coupée", e); else Log.Info("Liaison : fermée par la box");
            _ = StopAsync(e, raiseEnded: true, status: e == null ? "La box a fermé la session" : "Connexion avec la box perdue");
        };
        var hello = new Hello(1, Environment.MachineName, _opt.Width, _opt.Height, _opt.Fps, AudioFormat);
        await _link.ConnectAsync(new IPEndPoint(_box.Address, _box.Port), hello, ct);
        Log.Info($"Liaison : connectée, HELLO envoyé (nom « {hello.Name} », {hello.W}x{hello.H}@{hello.Fps}, {hello.Audio})");

        _encoder.Start();
        _capture.Start();

        if (_opt.Audio)
        {
            try
            {
                _audio = new AudioLoopback(_clock, SendAudio, SetStatus);
                _audio.Start();
                AudioActive = true;
            }
            catch (Exception e)
            {
                Log.Error("Son : indisponible, partage de l'image seule", e);
                try { _audio?.Dispose(); } catch { }
                _audio = null;
                AudioActive = false;
            }
        }

        _statsTimer = new System.Threading.Timer(_ => LogStats(), null, 5000, 5000);
        var enc = UsingHardwareEncoder ? $"encodage matériel ({EncoderName})" : $"encodage logiciel ({EncoderName}), plus lent";
        var snd = !_opt.Audio ? "sans son" : AudioActive ? "avec son" : "son indisponible";
        SetStatus($"Partage en cours vers {_box.Name} : {_opt.Width}x{_opt.Height} {_opt.Fps} i/s, {enc}, {snd}");
    }

    // Fil du pool de capture : rendre la main vite.
    private void OnFrame(ID3D11Texture2D tex, int contentW, int contentH, long ptsUs)
    {
        if (_stopping) return;
        Interlocked.Increment(ref _captured);
        if (!_pacer.ShouldAccept(ptsUs)) { Interlocked.Increment(ref _paced); return; }
        var slot = _slots.TryAcquireWrite();
        if (slot < 0) { Interlocked.Increment(ref _noSlot); return; }
        try
        {
            _conv!.Convert(tex, contentW, contentH, slot);
            _slots.Publish(slot, ptsUs);
            _encoder?.NotifyFrame();
        }
        catch (Exception e)
        {
            _slots.CancelWrite(slot);
            var n = Interlocked.Increment(ref _convertErrors);
            if (n <= 5 || n % 100 == 0) Log.Error($"Conversion NV12 impossible (n°{n})", e);
            if (n == 30) _ = StopAsync(e, raiseEnded: true, status: "Conversion de l'image impossible : partage arrêté");
        }
    }

    // Fil de l'encodeur : l'envoi bloque l'encodeur (on ne jette jamais une image encodée).
    private bool SendVideo(byte[] au, bool key, long ptsUs)
    {
        var link = _link;
        if (_stopping || link == null) return false;
        try
        {
            link.SendVideoAsync(au, key, ptsUs).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _videoSent);
            Interlocked.Add(ref _videoBytes, au.Length);
            return true;
        }
        catch (Exception e)
        {
            // ObjectDisposedException possible en synchrone après fermeture de LinkClient.
            if (!_stopping) Log.Warn($"Liaison : envoi vidéo impossible : {Log.Describe(e)}");
            return false;
        }
    }

    // Fil d'envoi du son.
    private bool SendAudio(ReadOnlyMemory<byte> pcm, long ptsUs)
    {
        var link = _link;
        if (_stopping || link == null) return false;
        try
        {
            link.SendAudioAsync(pcm, ptsUs).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e)
        {
            if (!_stopping) Log.Warn($"Liaison : envoi audio impossible : {Log.Describe(e)}");
            return false;
        }
    }

    private void LogStats()
    {
        if (_stopping) return;
        Log.Info($"Session : capture {Interlocked.Read(ref _captured)} images reçues, {Interlocked.Read(ref _paced)} ignorées (cadence), " +
                 $"{Interlocked.Read(ref _noSlot)} sans texture libre, {Interlocked.Read(ref _convertErrors)} erreurs ; " +
                 $"vidéo envoyée {Interlocked.Read(ref _videoSent)} UA / {Interlocked.Read(ref _videoBytes) / 1024} Kio ; " +
                 $"son {(_audio == null ? "inactif" : $"{_audio.Chunks} blocs, {_audio.Dropped} abandonnés")}");
    }

    private void SetStatus(string s)
    {
        lock (_statusLock) _status = s;
        Log.Info($"État : {s}");
        try { StatusChanged?.Invoke(s); } catch (Exception e) { Log.Warn($"StatusChanged : {Log.Describe(e)}"); }
    }

    private Task StopAsync(Exception? cause, bool raiseEnded, string? status = null)
    {
        lock (_statusLock)
        {
            if (_stopTask != null) return _stopTask;
            _stopping = true;
            // Toujours hors du fil appelant : l'arrêt attend les fils de capture, d'encodage et de liaison.
            _stopTask = Task.Run(() => StopCoreAsync(cause, raiseEnded, status));
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(Exception? cause, bool raiseEnded, string? status)
    {
        Log.Info($"Session : arrêt ({status ?? "demandé"})");
        try { _statsTimer?.Dispose(); } catch { }
        LogStatsFinal();
        try { _capture?.Dispose(); } catch (Exception e) { Log.Warn($"Arrêt capture : {Log.Describe(e)}"); }
        try { _audio?.Dispose(); } catch (Exception e) { Log.Warn($"Arrêt son : {Log.Describe(e)}"); }
        if (_encoder != null && !_encoder.Stop(TimeSpan.FromSeconds(2)) && _link != null)
        {
            // Encodeur coincé dans un envoi : fermer la liaison le débloque.
            try { await _link.DisposeAsync(); } catch { }
        }
        try { _encoder?.Dispose(); } catch (Exception e) { Log.Warn($"Arrêt encodeur : {Log.Describe(e)}"); }
        try { if (_link != null) await _link.DisposeAsync(); } catch (Exception e) { Log.Warn($"Arrêt liaison : {Log.Describe(e)}"); }
        try { _conv?.Dispose(); } catch (Exception e) { Log.Warn($"Arrêt convertisseur : {Log.Describe(e)}"); }
        try { _gpu?.Dispose(); } catch (Exception e) { Log.Warn($"Arrêt GPU : {Log.Describe(e)}"); }
        if (_mfStarted && _encoder?.LeakedTransform != true) { try { MediaFactory.MFShutdown(); } catch { } }
        else if (_mfStarted) Log.Warn("Session : MFShutdown sauté (MFT laissé en place par l'encodeur)");
        Log.Info("=== Session terminée");
        SetStatus(status ?? "Partage arrêté");
        if (raiseEnded) { try { Ended?.Invoke(cause); } catch (Exception e) { Log.Warn($"Ended : {Log.Describe(e)}"); } }
    }

    private void LogStatsFinal()
    {
        Log.Info($"Session : bilan capture {Interlocked.Read(ref _captured)} reçues / {Interlocked.Read(ref _paced)} ignorées / " +
                 $"{Interlocked.Read(ref _noSlot)} sans texture / {_slots.Replaced} remplacées ; vidéo {Interlocked.Read(ref _videoSent)} UA, " +
                 $"{Interlocked.Read(ref _videoBytes) / 1024} Kio");
    }

    public async ValueTask DisposeAsync()
    {
        var first = false;
        lock (_statusLock) first = _stopTask == null;
        await StopAsync(null, raiseEnded: first);
    }
}
