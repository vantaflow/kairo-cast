using System.Collections.Concurrent;
using KairoCast.App.Diagnostics;
using KairoCast.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KairoCast.App.Capture;

/// <summary>
/// Son du PC : WASAPI loopback du périphérique de sortie par défaut (NAudio), converti en s16le 48 kHz stéréo,
/// découpé en blocs de 10 ms datés sur l'horloge commune. L'envoi se fait sur un fil à part (file bornée)
/// pour ne jamais retenir le fil de capture WASAPI.
/// </summary>
internal sealed class AudioLoopback : IDisposable
{
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly MediaClock _clock;
    private readonly Func<ReadOnlyMemory<byte>, long, bool> _send;
    private readonly Action<string> _status;
    private readonly BlockingCollection<(ReadOnlyMemory<byte> pcm, long pts)> _queue = new(boundedCapacity: 50);
    private readonly PcmChunker _chunker = new(chunkFrames: 480);
    private WasapiLoopbackCapture? _capture;
    private PcmConverter? _converter;
    private Thread? _sender;
    private volatile bool _stopping;
    private long _chunks, _dropped, _callbacks;
    private bool _firstLogged;

    /// <param name="send">Envoi d'un bloc (octets, date µs) ; renvoie false si la liaison est morte.</param>
    public AudioLoopback(MediaClock clock, Func<ReadOnlyMemory<byte>, long, bool> send, Action<string> status)
    {
        _clock = clock;
        _send = send;
        _status = status;
    }

    public long Chunks => Interlocked.Read(ref _chunks);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Start()
    {
        using (var en = new MMDeviceEnumerator())
        {
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Log.Info($"Son : périphérique de sortie par défaut « {dev.FriendlyName} »");
        }
        _capture = new WasapiLoopbackCapture();
        var wf = _capture.WaveFormat;
        var fmt = ToPcmFormat(wf);
        _converter = new PcmConverter(fmt);
        Log.Info($"Son : format du mélangeur {wf} ({wf.Encoding}, {wf.BitsPerSample} bits, bloc {wf.BlockAlign}) → {fmt} ; " +
                 $"{(_converter.Resampling ? "rééchantillonnage linéaire vers 48 kHz" : "pas de rééchantillonnage")} ; sortie s16le 48 kHz stéréo, blocs de 10 ms");
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += OnStopped;
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "KairoCast envoi son" };
        _sender.Start();
        _capture.StartRecording();
        Log.Info("Son : capture démarrée");
    }

    private static PcmInputFormat ToPcmFormat(WaveFormat wf)
    {
        var encoding = wf.Encoding;
        var mask = 0;
        if (wf.Encoding == WaveFormatEncoding.Extensible)
        {
            var sub = (wf as WaveFormatExtensible)?.SubFormat ?? Guid.Empty;
            encoding = sub == SubtypeFloat ? WaveFormatEncoding.IeeeFloat
                     : sub == SubtypePcm ? WaveFormatEncoding.Pcm
                     : throw new NotSupportedException($"sous-format audio inconnu {sub}");
            mask = ReadChannelMask(wf);
        }
        var pcm = encoding switch
        {
            WaveFormatEncoding.IeeeFloat when wf.BitsPerSample == 32 => PcmEncoding.Float32,
            WaveFormatEncoding.Pcm when wf.BitsPerSample == 16 => PcmEncoding.Int16,
            WaveFormatEncoding.Pcm when wf.BitsPerSample == 24 => PcmEncoding.Int24,
            WaveFormatEncoding.Pcm when wf.BitsPerSample == 32 => PcmEncoding.Int32,
            _ => throw new NotSupportedException($"format audio non pris en charge : {wf.Encoding} {wf.BitsPerSample} bits"),
        };
        var f = new PcmInputFormat(wf.SampleRate, wf.Channels, pcm, mask);
        if (f.BlockAlign != wf.BlockAlign)
            throw new NotSupportedException($"alignement audio inattendu {wf.BlockAlign} (attendu {f.BlockAlign})");
        return f;
    }

    /// <summary>NAudio n'expose pas dwChannelMask : on le relit dans la sérialisation WAVEFORMATEXTENSIBLE (octet 20).</summary>
    private static int ReadChannelMask(WaveFormat wf)
    {
        try
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) wf.Serialize(bw);
            var b = ms.ToArray();
            return b.Length >= 24 ? BitConverter.ToInt32(b, 20) : 0;
        }
        catch { return 0; }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_stopping || e.BytesRecorded <= 0) return;
        try
        {
            var now = _clock.NowUs;
            Interlocked.Increment(ref _callbacks);
            var pcm = _converter!.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
            _chunker.Push(pcm, now, (chunk, pts) =>
            {
                if (!_queue.TryAdd((chunk, pts)))
                {
                    var d = Interlocked.Increment(ref _dropped);
                    if (d == 1 || d % 500 == 0) Log.Warn($"Son : envoi en retard, {d} blocs abandonnés");
                }
            });
            if (!_firstLogged)
            {
                _firstLogged = true;
                Log.Info($"Son : premières données {e.BytesRecorded} o capturés → {pcm.Length} o convertis");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Son : conversion impossible", ex);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;
        if (e.Exception != null)
        {
            Log.Error("Son : capture interrompue", e.Exception);
            _status("Son interrompu (périphérique de sortie changé ou indisponible)");
        }
        else Log.Info("Son : capture arrêtée");
    }

    private void SendLoop()
    {
        try
        {
            foreach (var (pcm, pts) in _queue.GetConsumingEnumerable())
            {
                if (_stopping) break;
                if (!_send(pcm, pts)) break;
                Interlocked.Increment(ref _chunks);
            }
        }
        catch (Exception e)
        {
            if (!_stopping) Log.Error("Son : fil d'envoi arrêté", e);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _capture?.StopRecording(); } catch (Exception e) { Log.Warn($"Son : arrêt : {Log.Describe(e)}"); }
        try { _capture?.Dispose(); } catch { }
        _queue.CompleteAdding();
        _sender?.Join(TimeSpan.FromSeconds(2));
        Log.Info($"Son arrêté ({Interlocked.Read(ref _callbacks)} tampons capturés, {Chunks} blocs envoyés, {Dropped} abandonnés)");
    }
}
