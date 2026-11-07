using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KairoCast.Link;

public sealed class LinkClient : IAsyncDisposable
{
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pingInterval;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private long _lastRxTicks;
    private int _disconnected;
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
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(ep, ct);
        _stream = _tcp.GetStream();
        Volatile.Write(ref _lastRxTicks, Stopwatch.GetTimestamp());
        await SendAsync(new LinkFrame(LinkType.Hello, 0, 0, hello.ToBytes()), ct);
        var t = _cts.Token;
        _loops = new[] { Task.Run(() => ReadLoop(t)), Task.Run(() => PingLoop(t)) };
    }

    public ValueTask SendVideoAsync(ReadOnlyMemory<byte> au, bool key, long ptsUs) =>
        SendAsyncValue(new LinkFrame(LinkType.Video, key ? 1 : 0, ptsUs, au.ToArray()));

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, long ptsUs) =>
        SendAsyncValue(new LinkFrame(LinkType.Audio, 0, ptsUs, pcm.ToArray()));

    private ValueTask SendAsyncValue(LinkFrame f) => new(SendAsync(f, _cts.Token));

    private async Task SendAsync(LinkFrame f, CancellationToken ct)
    {
        var s = _stream ?? throw new InvalidOperationException("non connecte");
        var bytes = LinkCodec.Encode(f);
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
                if (f.Type == LinkType.KeyReq) KeyFrameRequested?.Invoke();
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
                await SendAsync(new LinkFrame(LinkType.Ping, 0, 0, Array.Empty<byte>()), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }

    private void Fail(Exception? e)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1) return;
        _cts.Cancel();
        try { _tcp?.Close(); } catch { }
        Disconnected?.Invoke(e);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 0 && _stream != null)
        {
            try { await SendAsync(new LinkFrame(LinkType.Bye, 0, 0, Array.Empty<byte>()), CancellationToken.None); } catch { }
        }
        _cts.Cancel();
        try { _tcp?.Close(); } catch { }
        try { await Task.WhenAll(_loops); } catch { }
        _cts.Dispose();
    }
}
