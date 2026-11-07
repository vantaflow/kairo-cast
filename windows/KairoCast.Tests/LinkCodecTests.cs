using KairoCast.Link;

namespace KairoCast.Tests;

public class LinkCodecTests
{
    private static byte[] Vec(string n) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", n + ".bin"));
    private static Task<LinkFrame?> Read(byte[] b) => LinkCodec.ReadAsync(new MemoryStream(b), CancellationToken.None);

    [Theory]
    [InlineData("hello")] [InlineData("video_key")] [InlineData("audio")]
    [InlineData("ping")] [InlineData("keyreq")] [InlineData("bye")]
    public async Task RoundTrip(string name)
    {
        var b = Vec(name);
        var f = await Read(b);
        Assert.NotNull(f);
        Assert.Equal(b, LinkCodec.Encode(f!));
    }

    [Fact]
    public async Task VideoKeyFields()
    {
        var f = (await Read(Vec("video_key")))!;
        Assert.Equal(LinkType.Video, f.Type);
        Assert.Equal(1, f.Flags);
        Assert.Equal(123456L, f.PtsUs);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0, 0x1f }, f.Payload);
    }

    [Fact]
    public async Task AudioFields()
    {
        var f = (await Read(Vec("audio")))!;
        Assert.Equal(LinkType.Audio, f.Type);
        Assert.Equal(1000L, f.PtsUs);
        Assert.Equal(1920, f.Payload.Length);
    }

    [Fact]
    public async Task HelloParse()
    {
        var h = Hello.Parse((await Read(Vec("hello")))!.Payload);
        Assert.Equal(new Hello(1, "PC-Salon", 1920, 1080, 30, "pcm_s16le_48000_2"), h);
        Assert.Equal(h, Hello.Parse(h.ToBytes()));
    }

    [Fact]
    public async Task EmptyStreamIsNull() => Assert.Null(await Read(Array.Empty<byte>()));

    [Theory]
    [InlineData("badmagic")] [InlineData("huge")] [InlineData("truncated")]
    public async Task InvalidThrows(string name) =>
        await Assert.ThrowsAsync<LinkProtocolException>(() => Read(Vec(name)));

    [Fact]
    public async Task TruncatedHeaderThrows() =>
        await Assert.ThrowsAsync<LinkProtocolException>(() => Read(Vec("video_key")[..7]));

    [Fact]
    public async Task UnknownTypeThrows()
    {
        var b = Vec("ping");
        b[4] = 99;
        await Assert.ThrowsAsync<LinkProtocolException>(() => Read(b));
    }
}
