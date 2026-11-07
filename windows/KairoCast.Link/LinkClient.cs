using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KairoCast.Link;

public sealed class LinkClient : IAsyncDisposable
{
    private static readonly TimeSpan ByeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pingInterval;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private long _lastRxTicks;
    private int _disconnected;
    private int _connectCalled;
    private int _disposed;
    private Task[] _loops = Array.Empty<Task>();

    public event Action? KeyFrameRequested;
    public event Action<Exception?>? Disconnected;

    public LinkClient(TimeSpan? timeout = null, TimeSpan? pingInterval = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
        _pingInterval = pingInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task ConnectAsync(IPEndPoint ep, Hello hello, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(LinkClient));
        if (Interlocked.Exchange(ref _connectCalled, 1) == 1)
            throw new InvalidOperationException("ConnectAsync deja appele");
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            await tcp.ConnectAsync(ep, linked.Token);
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(LinkClient));
            _tcp = tcp;
            _stream = tcp.GetStream();
            Volatile.Write(ref _lastRxTicks, Stopwatch.GetTimestamp());
            await SendAsync(LinkCodec.Encode(LinkType.Hello, 0, 0, hello.ToBytes()), linked.Token);
        }
        catch
        {
            try { tcp.Close(); } catch { }
            throw;
        }
        var t = _cts.Token;
        _loops = new[] { Task.Run(() => ReadLoop(t)), Task.Run(() => PingLoop(t)) };
    }

    public ValueTask SendVideoAsync(ReadOnlyMemory<byte> au, bool key, long ptsUs) =>
        new(SendAsync(LinkCodec.Encode(LinkType.Video, key ? 1 : 0, ptsUs, au.Span), _cts.Token));

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, long ptsUs) =>
        new(SendAsync(LinkCodec.Encode(LinkType.Audio, 0, ptsUs, pcm.Span), _cts.Token));

    private static byte[] Empty(LinkType t) => LinkCodec.Encode(t, 0, 0, ReadOnlySpan<byte>.Empty);

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        var s = _stream ?? throw new InvalidOperationException("non connecte");
        await _writeLock.WaitAsync(ct);
        try
        {
            await s.WriteAsync(bytes, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fail(e);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var f = await LinkCodec.ReadAsync(_stream!, ct);
                if (f == null) { Fail(null); return; }
                Volatile.Write(ref _lastRxTicks, Stopwatch.GetTimestamp());
                if (f.Type == LinkType.KeyReq) RaiseOffLoop(() => KeyFrameRequested?.Invoke());
                else if (f.Type == LinkType.Bye) { Fail(null); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }

    private async Task PingLoop(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_pingInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastRxTicks)) > _timeout)
                {
                    Fail(new TimeoutException("aucune donnee recue"));
                    return;
                }
                await SendAsync(Empty(LinkType.Ping), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }

    // Les gestionnaires s'executent hors des boucles : ils peuvent attendre DisposeAsync, et leurs exceptions sont isolees.
    private static void RaiseOffLoop(Action a) =>
        _ = Task.Run(() => { try { a(); } catch { } });

    private void Fail(Exception? e)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _tcp?.Close(); } catch { }
        RaiseOffLoop(() => Disconnected?.Invoke(e));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (Interlocked.Exchange(ref _disconnected, 1) == 0 && _stream != null)
        {
            // BYE au mieux : ne jamais bloquer si une ecriture est coincee.
            using var bye = new CancellationTokenSource(ByeTimeout);
            try { await SendAsync(Empty(LinkType.Bye), bye.Token); } catch { }
        }
        _cts.Cancel();
        try { _tcp?.Close(); } catch { }
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
