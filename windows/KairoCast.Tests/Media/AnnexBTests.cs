using KairoCast.Media;

namespace KairoCast.Tests.Media;

public class AnnexBTests
{
    private static readonly byte[] Sps = { 0, 0, 0, 1, 0x67, 0x4D, 0x40, 0x28, 0xAA };
    private static readonly byte[] Pps = { 0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80 };
    private static readonly byte[] Idr = { 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x10 };
    private static readonly byte[] Slice = { 0, 0, 0, 1, 0x41, 0x9A, 0x02 };
    private static readonly byte[] Aud = { 0, 0, 0, 1, 0x09, 0xF0 };

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void SplitNals_FindsTypesAndRangesWith3And4ByteStartCodes()
    {
        var au = Cat(Sps, Pps, Idr);
        var nals = AnnexB.SplitNals(au);
        Assert.Equal(new[] { 7, 8, 5 }, nals.Select(n => n.Type));
        Assert.Equal(0, nals[0].Offset);
        Assert.Equal(Sps.Length, nals[0].Length);
        Assert.Equal(Sps.Length + Pps.Length, nals[2].Offset);
        Assert.Equal(Idr.Length, nals[2].Length);
        Assert.Equal(4, nals[0].PayloadOffset - nals[0].Offset);
        Assert.Equal(3, nals[2].PayloadOffset - nals[2].Offset);
    }

    [Fact]
    public void SplitNals_IgnoresBytesWithoutStartCode()
    {
        Assert.Empty(AnnexB.SplitNals(new byte[] { 0x65, 0x01, 0x02 }));
        Assert.Empty(AnnexB.SplitNals(Array.Empty<byte>()));
    }

    [Fact]
    public void IsIdr_DetectsType5()
    {
        Assert.True(AnnexB.IsIdr(Cat(Sps, Pps, Idr)));
        Assert.False(AnnexB.IsIdr(Slice));
    }

    [Fact]
    public void Cache_PrependsCachedSpsPpsWhenIdrLacksThem()
    {
        var cache = new ParameterSetCache();
        var first = cache.Prepare(Cat(Sps, Pps, Idr), out var key1);
        Assert.True(key1);
        Assert.Equal(Cat(Sps, Pps, Idr), first);

        var bare = cache.Prepare(Idr, out var key2);
        Assert.True(key2);
        Assert.Equal(Cat(Sps, Pps, Idr), bare);

        var p = cache.Prepare(Slice, out var key3);
        Assert.False(key3);
        Assert.Equal(Slice, p);
    }

    [Fact]
    public void Cache_InsertsAfterAccessUnitDelimiter()
    {
        var cache = new ParameterSetCache();
        cache.LoadSequenceHeader(Cat(Sps, Pps));
        var outp = cache.Prepare(Cat(Aud, Idr), out var key);
        Assert.True(key);
        Assert.Equal(Cat(Aud, Sps, Pps, Idr), outp);
    }

    [Fact]
    public void Cache_WithoutParameterSets_ReturnsIdrUnchanged()
    {
        var cache = new ParameterSetCache();
        Assert.False(cache.HasParameterSets);
        Assert.Equal(Idr, cache.Prepare(Idr, out var key));
        Assert.True(key);
    }

    [Fact]
    public void Cache_UpdatesWhenEncoderSendsNewSps()
    {
        var cache = new ParameterSetCache();
        cache.Prepare(Cat(Sps, Pps, Idr), out _);
        var sps2 = new byte[] { 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28 };
        cache.Prepare(Cat(sps2, Pps, Idr), out _);
        Assert.Equal(Cat(sps2, Pps, Idr), cache.Prepare(Idr, out _));
    }

    [Fact]
    public void FromLengthPrefixed_ConvertsAvccToAnnexB()
    {
        var avcc = new byte[] { 0, 0, 0, 3, 0x65, 0xAA, 0xBB, 0, 0, 0, 2, 0x41, 0xCC };
        Assert.False(AnnexB.HasStartCode(avcc));
        var annexB = AnnexB.FromLengthPrefixed(avcc);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0xAA, 0xBB, 0, 0, 0, 1, 0x41, 0xCC }, annexB);
        Assert.True(AnnexB.HasStartCode(annexB));
        Assert.Null(AnnexB.FromLengthPrefixed(new byte[] { 0, 0, 0, 9, 0x65 }));
    }

    [Fact]
    public void Describe_ListsNalTypes()
    {
        Assert.Equal("7,8,5", AnnexB.Describe(Cat(Sps, Pps, Idr)));
    }
}
