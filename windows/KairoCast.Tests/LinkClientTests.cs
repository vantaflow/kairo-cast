using System.Net;
using System.Net.Sockets;
using KairoCast.Link;

namespace KairoCast.Tests;

public class LinkClientTests
{
    private static readonly Hello H = new(1, "PC-Test", 1280, 720, 30, "pcm_s16le_48000_2");

    private static (TcpListener, IPEndPoint) Listen()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return (l, (IPEndPoint)l.LocalEndpoint);
    }

    [Fact]
    public async Task HelloIsFirstThenFramesInOrder()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2));
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept;
        var s = srv.GetStream();
        var first = (await LinkCodec.ReadAsync(s, CancellationToken.None))!;
        Assert.Equal(LinkType.Hello, first.Type);
        Assert.Equal(H, Hello.Parse(first.Payload));
        await c.SendVideoAsync(new byte[] { 0, 0, 0, 1, 0x65 }, true, 42);
        await c.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, 43);
        var v = (await LinkCodec.ReadAsync(s, CancellationToken.None))!;
        Assert.Equal(LinkType.Video, v.Type);
        Assert.Equal(1, v.Flags);
        Assert.Equal(42L, v.PtsUs);
        var a = (await LinkCodec.ReadAsync(s, CancellationToken.None))!;
        Assert.Equal(LinkType.Audio, a.Type);
        Assert.Equal(43L, a.PtsUs);
        l.Stop();
    }

    [Fact]
    public async Task KeyReqRaisesEvent()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2));
        var got = new TaskCompletionSource();
        c.KeyFrameRequested += () => got.TrySetResult();
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept;
        var bytes = LinkCodec.Encode(new LinkFrame(LinkType.KeyReq, 0, 0, Array.Empty<byte>()));
        await srv.GetStream().WriteAsync(bytes);
        await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        l.Stop();
    }

    [Fact]
    public async Task SilentServerTriggersDisconnected()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
        var got = new TaskCompletionSource<Exception?>();
        c.Disconnected += e => got.TrySetResult(e);
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept;
        await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        l.Stop();
    }

    [Fact]
    public async Task PingsAreSentPeriodically()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient(TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(100));
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept;
        var s = srv.GetStream();
        await LinkCodec.ReadAsync(s, CancellationToken.None); // HELLO
        var p = (await LinkCodec.ReadAsync(s, CancellationToken.None))!;
        Assert.Equal(LinkType.Ping, p.Type);
        l.Stop();
    }

    [Fact]
    public async Task ServerCloseTriggersDisconnected()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2));
        var got = new TaskCompletionSource<Exception?>();
        c.Disconnected += e => got.TrySetResult(e);
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        (await accept).Close();
        await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        l.Stop();
    }

    [Fact]
    public async Task HandlerAwaitingDisposeDoesNotDeadlock()
    {
        var (l, ep) = Listen();
        var c = new LinkClient(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2));
        var done = new TaskCompletionSource();
        c.KeyFrameRequested += async () => { await c.DisposeAsync(); done.TrySetResult(); };
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept;
        await srv.GetStream().WriteAsync(LinkCodec.Encode(new LinkFrame(LinkType.KeyReq, 0, 0, Array.Empty<byte>())));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        l.Stop();
    }

    [Fact]
    public async Task ThrowingHandlerDoesNotKillLink()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2));
        var count = 0;
        var second = new TaskCompletionSource();
        c.KeyFrameRequested += () => { if (Interlocked.Increment(ref count) == 1) throw new Exception("boom"); second.TrySetResult(); };
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept;
        var req = LinkCodec.Encode(new LinkFrame(LinkType.KeyReq, 0, 0, Array.Empty<byte>()));
        await srv.GetStream().WriteAsync(req);
        await srv.GetStream().WriteAsync(req);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        l.Stop();
    }

    [Fact]
    public async Task SecondConnectIsRejected()
    {
        var (l, ep) = Listen();
        await using var c = new LinkClient();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.ConnectAsync(ep, H, CancellationToken.None));
        l.Stop();
    }

    [Fact]
    public async Task ConnectFailureThrowsAndDisposeIsSafe()
    {
        var (l, ep) = Listen();
        l.Stop();
        var c = new LinkClient();
        await Assert.ThrowsAnyAsync<Exception>(() => c.ConnectAsync(ep, H, CancellationToken.None));
        await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DisposeWithStalledWriteDoesNotHang()
    {
        var (l, ep) = Listen();
        var c = new LinkClient(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(ep, H, CancellationToken.None);
        using var srv = await accept; // le serveur ne lit jamais
        var big = new byte[LinkCodec.MaxPayload];
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) await c.SendVideoAsync(big, false, 0); } catch { }
        });
        await Task.Delay(500); // les tampons TCP sont pleins, une ecriture est bloquee
        await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        l.Stop();
    }
}
