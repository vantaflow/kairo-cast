using System.Diagnostics;
using System.Net.Sockets;
using KairoCast.Core;
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
    /// <summary>Fin de session ; message d'erreur ou null si arrêt normal. Jamais sur le thread de l'interface.</summary>
    public event Action<string?>? Ended;

    public string? Encoder { get; private set; }

    long Pts => clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    void ThrowIfStopped()
    {
        if (Volatile.Read(ref stopped) == 1) throw new OperationCanceledException();
    }

    public async Task StartAsync(Receiver receiver, int output, Rectangle screen, bool withAudio, int fps)
    {
        Status?.Invoke($"Connexion à {receiver.Name}…");
        tcp = new TcpClient { NoDelay = true, SendBufferSize = 1 << 20 };
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            t.CancelAfter(TimeSpan.FromSeconds(4));
            try { await tcp.ConnectAsync(receiver.Address, receiver.Port, t.Token); }
            catch (OperationCanceledException) { ThrowIfStopped(); throw new IOException($"{receiver.Name} ne répond pas."); }
        }
        var s = tcp.GetStream();
        s.ReadTimeout = 4000;
        s.WriteTimeout = 5000; // un réseau bloqué ne fige jamais l'envoi plus de 5 s
        await s.WriteAsync(LinkProtocol.Hello(Environment.MachineName), stop.Token);
        var reply = LinkProtocol.ReadReply(s);
        if (!reply.Accepted)
            throw new IOException(reply.RefuseReason == LinkProtocol.RefuseBusy
                ? $"{receiver.Name} reçoit déjà une recopie."
                : "Version de Kairo Cast incompatible : mettez à jour l'application sur la box.");
        s.ReadTimeout = Timeout.Infinite;
        lock (writeLock) net = new BufferedStream(s, 1 << 16);
        // Ping dès la poignée de main : la box coupe après 5 s de silence, la recherche d'encodeur peut durer.
        _ = Task.Run(PingLoop);
        _ = Task.Run(WatchReceiver);

        var (w, h) = Ffmpeg.Fit(screen.Width, screen.Height, reply.MaxWidth, reply.MaxHeight);
        Send(LinkProtocol.VideoFormatType, 0, LinkProtocol.VideoFormat(w, h));
        await StartVideoAsync(output, w, h, fps);
        ThrowIfStopped();
        if (withAudio && reply.Audio) StartAudio();
        Status?.Invoke($"Recopie vers {receiver.Name} — {w}×{h}, {fps} i/s ({Encoder})");
    }

    async Task StartVideoAsync(int output, int w, int h, int fps)
    {
        string exe = FfmpegLocator.Path();
        string? lastError = null;
        foreach (var enc in Ffmpeg.Encoders)
        {
            ThrowIfStopped();
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
            ChildProcesses.Attach(p); // tué avec Kairo Cast, même en cas de plantage
            lock (writeLock) ffmpeg = p; // visible par End pendant l'essai
            p.BeginErrorReadLine();
            var stdout = p.StandardOutput.BaseStream;
            var first = new byte[64 * 1024];
            var read = stdout.ReadAsync(first, 0, first.Length);
            var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(6), stop.Token).ContinueWith(_ => { }));
            if (done == read && read.Status == TaskStatus.RanToCompletion && read.Result > 0 && Volatile.Read(ref stopped) == 0)
            {
                Encoder = enc;
                _ = Task.Run(() => PumpVideo(stdout, first, read.Result));
                return;
            }
            lock (err) lastError = err.ToString().Trim();
            lock (writeLock) ffmpeg = null;
            Kill(p);
        }
        ThrowIfStopped();
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
            var cap = new WasapiLoopbackCapture();
            var f = cap.WaveFormat;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                || (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            if (!(isFloat && f.BitsPerSample == 32) && f.BitsPerSample != 16)
            {
                Status?.Invoke("Son non pris en charge par ce PC : recopie sans son.");
                cap.Dispose();
                return;
            }
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0 || stop.IsCancellationRequested) return;
                var raw = isFloat ? Pcm.FloatToS16(e.Buffer.AsSpan(0, e.BytesRecorded)) : e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
                var pcm = Pcm.ToStereo(raw, f.Channels);
                try
                {
                    if (!audioFormatSent)
                    {
                        Send(LinkProtocol.AudioFormatType, 0, LinkProtocol.AudioFormat(f.SampleRate, Math.Min(2, f.Channels)));
                        audioFormatSent = true;
                    }
                    Send(LinkProtocol.Audio, Pts, pcm);
                }
                // Jamais End sur le thread de capture : Dispose y attendrait sa propre fin.
                catch (Exception ex) { var m = Message(ex); Task.Run(() => End(m)); }
            };
            lock (writeLock)
            {
                if (stop.IsCancellationRequested) { cap.Dispose(); return; }
                loopback = cap;
            }
            cap.StartRecording();
        }
        catch (Exception e)
        {
            Status?.Invoke("Son indisponible : " + e.Message);
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

    static string Message(Exception e) => e is IOException or SocketException or ObjectDisposedException
        ? "Connexion avec la box perdue."
        : e.Message;

    static void Kill(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
    }

    /// <summary>Arrêt sans bloquer l'appelant (thread de l'interface).</summary>
    public void Stop() => Task.Run(() => End(null));

    void End(string? error)
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1) return;
        // FIN en best-effort, sans attendre un envoi bloqué par le réseau.
        if (Monitor.TryEnter(writeLock, 300))
        {
            try { if (net != null) { LinkProtocol.WritePacket(net, LinkProtocol.Bye, Pts, ReadOnlySpan<byte>.Empty); net.Flush(); } }
            catch { }
            finally { Monitor.Exit(writeLock); }
        }
        stop.Cancel();
        try { tcp?.Close(); } catch { } // débloque un envoi en cours
        Process? p; WasapiLoopbackCapture? cap;
        lock (writeLock) { net = null; p = ffmpeg; ffmpeg = null; cap = loopback; loopback = null; }
        Kill(p);
        if (cap != null)
        {
            try { cap.StopRecording(); } catch { }
            Task.Run(() => { try { cap.Dispose(); } catch { } });
        }
        Ended?.Invoke(error);
    }

    public void Dispose() => End(null);
}
