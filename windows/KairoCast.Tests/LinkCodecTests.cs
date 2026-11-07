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

    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _i;
        public override int Read(byte[] buffer, int offset, int count) =>
            _i >= data.Length || count == 0 ? 0 : (buffer[offset] = data[_i++]) * 0 + 1;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_i >= data.Length || buffer.Length == 0) return new(0);
            buffer.Span[0] = data[_i++];
            return new(1);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ByteAtATimeStream()
    {
        var b = Vec("audio");
        var f = await LinkCodec.ReadAsync(new OneByteStream(b), CancellationToken.None);
        Assert.Equal(b, LinkCodec.Encode(f!));
    }

    [Fact]
    public void HelloMissingFieldThrows() =>
        Assert.Throws<LinkProtocolException>(() =>
            Hello.Parse("{\"v\":1,\"w\":1,\"h\":1,\"fps\":30,\"audio\":\"x\"}"u8));

    [Fact]
    public void EncodeOversizeThrows() =>
        Assert.Throws<LinkProtocolException>(() =>
            LinkCodec.Encode(new LinkFrame(LinkType.Video, 0, 0, new byte[LinkCodec.MaxPayload + 1])));
}
