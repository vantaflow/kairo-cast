using System.Net;
using System.Net.Sockets;
using System.Text;
using KairoCast.Link;

namespace KairoCast.Tests;

public class BoxStatusTests
{
    [Theory]
    [InlineData("""{"busy":false,"source":"","by":""}""", "libre")]
    [InlineData("""{"busy":true,"source":"link","by":"PC Bureau"}""", "occupée par PC Bureau")]
    [InlineData("""{"busy":true,"source":"link","by":""}""", "occupée")]
    [InlineData("""{"busy":true,"source":"airplay","by":""}""", "occupée (Mac ou iPhone)")]
    public void DescribesTheStatus(string json, string text) =>
        Assert.Equal(text, BoxStatus.Parse(Encoding.UTF8.GetBytes(json))!.Describe());

    [Theory]
    [InlineData("{}")]
    [InlineData("pas du json")]
    [InlineData("""{"busy":"oui"}""")]
    public void UnknownAnswerIsNull(string json) => Assert.Null(BoxStatus.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task QueryReadsTheStatusFrame()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var server = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var q = await LinkCodec.ReadAsync(s, CancellationToken.None);
            Assert.Equal(LinkType.Status, q!.Type);
            await s.WriteAsync(LinkCodec.Encode(LinkType.Status, 0, 0, """{"busy":true,"source":"link","by":"PC"}"""u8));
        });
        var st = await BoxStatus.QueryAsync((IPEndPoint)l.LocalEndpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
        await server;
        l.Stop();
        Assert.Equal(new BoxStatus(true, "link", "PC"), st);
    }

    [Fact]
    public async Task OldBoxThatClosesGivesNull()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var server = Task.Run(async () => { using var c = await l.AcceptTcpClientAsync(); await Task.Delay(50); });
        var st = await BoxStatus.QueryAsync((IPEndPoint)l.LocalEndpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
        await server;
        l.Stop();
        Assert.Null(st);
    }
}
