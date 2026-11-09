using System.Diagnostics;
using System.Net.Sockets;
using KairoCast.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KairoCast;

/// <summary>Une session de recopie : ffmpeg (capture + H.264), son WASAPI en boucle, envoi Kairo Link.</summary>
public sealed class Sender : IDisposable
{
    readonly object writeLock = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly CancellationTokenSource stop = new();
    TcpClient? tcp;
    BufferedStream? net;
    Process? ffmpeg;
    WasapiLoopbackCapture? loopback;
    bool audioFormatSent;
    int stopped;

    public event Action<string>? Status;
    /// <summary>Fin de session ; message d'erreur ou null si arrêt normal.</summary>
    public event Action<string?>? Ended;

    public string? Encoder { get; private set; }

    long Pts => clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    public async Task StartAsync(Receiver receiver, int output, Rectangle screen, bool withAudio, int fps)
    {
        Status?.Invoke($"Connexion à {receiver.Name}…");
        tcp = new TcpClient { NoDelay = true, SendBufferSize = 1 << 20 };
        using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
        {
            try { await tcp.ConnectAsync(receiver.Address, receiver.Port, t.Token); }
            catch (OperationCanceledException) { throw new IOException($"{receiver.Name} ne répond pas."); }
        }
        var s = tcp.GetStream();
        s.ReadTimeout = 4000;
        await s.WriteAsync(LinkProtocol.Hello(Environment.MachineName));
        var reply = LinkProtocol.ReadReply(s);
        if (!reply.Accepted)
            throw new IOException(reply.RefuseReason == LinkProtocol.RefuseBusy
                ? $"{receiver.Name} reçoit déjà une recopie."
                : "Version de Kairo Cast incompatible : mettez à jour l'application sur la box.");
        s.ReadTimeout = Timeout.Infinite;
        net = new BufferedStream(s, 1 << 16);

        var (w, h) = Ffmpeg.Fit(screen.Width, screen.Height, reply.MaxWidth, reply.MaxHeight);
        Send(LinkProtocol.VideoFormatType, 0, LinkProtocol.VideoFormat(w, h));
        await StartVideoAsync(output, w, h, fps);
        if (withAudio && reply.Audio) StartAudio();

        _ = Task.Run(PingLoop);
        _ = Task.Run(WatchReceiver);
        Status?.Invoke($"Recopie vers {receiver.Name} — {w}×{h}, {fps} i/s ({Encoder})");
    }

    async Task StartVideoAsync(int output, int w, int h, int fps)
    {
        string exe = FfmpegLocator.Path();
        string? lastError = null;
        foreach (var enc in Ffmpeg.Encoders)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, Ffmpeg.Arguments(enc, output, w, h, fps, Ffmpeg.Bitrate(w, h, fps)))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            var err = new System.Text.StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.Start();
            p.BeginErrorReadLine();
            var stdout = p.StandardOutput.BaseStream;
            var first = new byte[64 * 1024];
            var read = stdout.ReadAsync(first, 0, first.Length);
            var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(6)));
            if (done == read && read.Result > 0)
            {
                Encoder = enc;
                ffmpeg = p;
                _ = Task.Run(() => PumpVideo(stdout, first, read.Result));
                return;
            }
            lock (err) lastError = err.ToString().Trim();
            try { if (!p.HasExited) p.Kill(); } catch { }
            p.Dispose();
        }
        throw new IOException("Aucun encodeur vidéo n'a démarré." + (string.IsNullOrEmpty(lastError) ? "" : "\n" + lastError));
    }

    void PumpVideo(Stream stdout, byte[] first, int n)
    {
        var splitter = new AccessUnitSplitter();
        var buf = new byte[256 * 1024];
        try
        {
            foreach (var au in splitter.Push(first.AsSpan(0, n))) Send(LinkProtocol.Video, Pts, au);
            while (!stop.IsCancellationRequested)
            {
                int r = stdout.Read(buf, 0, buf.Length);
                if (r <= 0) break;
                foreach (var au in splitter.Push(buf.AsSpan(0, r))) Send(LinkProtocol.Video, Pts, au);
            }
            End(stop.IsCancellationRequested ? null : "La capture de l'écran s'est arrêtée.");
        }
        catch (Exception e) { End(Message(e)); }
    }

    void StartAudio()
    {
        try
        {
            loopback = new WasapiLoopbackCapture();
            var f = loopback.WaveFormat;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                || (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            if (!(isFloat && f.BitsPerSample == 32) && f.BitsPerSample != 16)
            {
                Status?.Invoke("Son non pris en charge par ce PC : recopie sans son.");
                loopback.Dispose(); loopback = null;
                return;
            }
            loopback.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var pcm = Pcm.ToStereo(isFloat ? Pcm.FloatToS16(e.Buffer.AsSpan(0, e.BytesRecorded)) : e.Buffer.AsSpan(0, e.BytesRecorded).ToArray(), f.Channels);
                try
                {
                    if (!audioFormatSent)
                    {
                        Send(LinkProtocol.AudioFormatType, 0, LinkProtocol.AudioFormat(f.SampleRate, Math.Min(2, f.Channels)));
                        audioFormatSent = true;
                    }
                    Send(LinkProtocol.Audio, Pts, pcm);
                }
                catch (Exception ex) { End(Message(ex)); }
            };
            loopback.StartRecording();
        }
        catch (Exception e)
        {
            Status?.Invoke("Son indisponible : " + e.Message);
            loopback?.Dispose(); loopback = null;
        }
    }

    async Task PingLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop.Token);
                Send(LinkProtocol.Ping, Pts, ReadOnlySpan<byte>.Empty);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { End(Message(e)); }
    }

    /// <summary>La box n'envoie rien après la poignée de main : une lecture qui se termine = connexion fermée.</summary>
    async Task WatchReceiver()
    {
        try
        {
            var b = new byte[16];
            while (!stop.IsCancellationRequested)
                if (await tcp!.GetStream().ReadAsync(b, stop.Token) <= 0) break;
            if (!stop.IsCancellationRequested) End("La box a fermé la recopie.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!stop.IsCancellationRequested) End(Message(e)); }
    }

    void Send(byte type, long pts, ReadOnlySpan<byte> payload)
    {
        lock (writeLock)
        {
            if (net == null || stop.IsCancellationRequested) return;
            LinkProtocol.WritePacket(net, type, pts, payload);
            net.Flush();
        }
    }

    static string Message(Exception e) => e is IOException or SocketException
        ? "Connexion avec la box perdue."
        : e.Message;

    public void Stop() => End(null);

    void End(string? error)
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1) return;
        try { lock (writeLock) { if (net != null) { LinkProtocol.WritePacket(net, LinkProtocol.Bye, Pts, ReadOnlySpan<byte>.Empty); net.Flush(); } } } catch { }
        stop.Cancel();
        try { loopback?.StopRecording(); } catch { }
        loopback?.Dispose();
        try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(); } catch { }
        ffmpeg?.Dispose();
        try { tcp?.Close(); } catch { }
        lock (writeLock) net = null;
        Ended?.Invoke(error);
    }

    public void Dispose() => End(null);
}
