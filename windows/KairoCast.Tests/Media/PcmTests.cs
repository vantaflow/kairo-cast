using System.Buffers.Binary;
using KairoCast.Media;

namespace KairoCast.Tests.Media;

public class PcmTests
{
    private static byte[] Floats(params float[] v)
    {
        var b = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    private static short[] Shorts(byte[] b)
    {
        var s = new short[b.Length / 2];
        for (var i = 0; i < s.Length; i++) s[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2));
        return s;
    }

    [Fact]
    public void Float48kStereo_ConvertsToS16WithClamping()
    {
        var c = new PcmConverter(new PcmInputFormat(48000, 2, PcmEncoding.Float32));
        var outp = c.Convert(Floats(0f, 1f, -1f, 0.5f, 2f, -3f));
        Assert.Equal(new short[] { 0, 32767, -32768, 16384, 32767, -32768 }, Shorts(outp));
    }

    [Fact]
    public void Int16Mono_IsDuplicatedOnBothChannels()
    {
        var c = new PcmConverter(new PcmInputFormat(48000, 1, PcmEncoding.Int16));
        var src = new byte[] { 0x10, 0x00, 0xF0, 0xFF };
        Assert.Equal(new short[] { 16, 16, -16, -16 }, Shorts(c.Convert(src)));
    }

    [Fact]
    public void Int24AndInt32_AreDecoded()
    {
        var c24 = new PcmConverter(new PcmInputFormat(48000, 2, PcmEncoding.Int24));
        // 0x400000 = 0.5 ; 0xC00000 = -0.5
        Assert.Equal(new short[] { 16384, -16384 }, Shorts(c24.Convert(new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 })));
        var c32 = new PcmConverter(new PcmInputFormat(48000, 2, PcmEncoding.Int32));
        var b = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b, int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), int.MinValue);
        Assert.Equal(new short[] { 32767, -32768 }, Shorts(c32.Convert(b)));
    }

    [Fact]
    public void Surround51_IsDownmixedToStereo()
    {
        var c = new PcmConverter(new PcmInputFormat(48000, 6, PcmEncoding.Float32));
        // FL=0.5 seul -> gauche seulement
        var l = Shorts(c.Convert(Floats(0.5f, 0, 0, 0, 0, 0)));
        Assert.True(l[0] > 0 && l[1] == 0);
        // FC -> les deux canaux à égalité ; LFE ignoré
        var cc = Shorts(c.Convert(Floats(0, 0, 0.5f, 0.9f, 0, 0)));
        Assert.True(cc[0] > 0 && cc[0] == cc[1]);
        // pleine échelle sur tous les canaux : pas d'écrêtage
        var full = Shorts(c.Convert(Floats(1, 1, 1, 1, 1, 1)));
        Assert.True(full[0] <= 32767 && full[0] > 30000);
    }

    [Fact]
    public void Resampling44100_ProducesExpectedFrameCountAcrossCalls()
    {
        var c = new PcmConverter(new PcmInputFormat(44100, 2, PcmEncoding.Float32));
        var total = 0;
        for (var i = 0; i < 100; i++) total += c.Convert(Floats(new float[441 * 2])).Length / 4; // 10 ms par appel
        Assert.InRange(total, 48000 - 2, 48000); // 1 s à 48 kHz
    }

    [Fact]
    public void Resampling_KeepsDcLevel()
    {
        var c = new PcmConverter(new PcmInputFormat(44100, 2, PcmEncoding.Float32));
        var src = Enumerable.Repeat(0.25f, 882 * 2).ToArray();
        var s = Shorts(c.Convert(Floats(src)));
        Assert.All(s.Skip(4), v => Assert.InRange(v, 8190, 8194));
    }

    [Fact]
    public void Convert_IgnoresTrailingPartialFrameButKeepsItForNextCall()
    {
        var c = new PcmConverter(new PcmInputFormat(48000, 2, PcmEncoding.Int16));
        var a = c.Convert(new byte[] { 1, 0, 2, 0, 3 });
        Assert.Equal(new short[] { 1, 2 }, Shorts(a));
        var b = c.Convert(new byte[] { 0, 4, 0 });
        Assert.Equal(new short[] { 3, 4 }, Shorts(b));
    }

    [Fact]
    public void Chunker_Emits10msChunksWithTimestampsFromCaptureTime()
    {
        var ch = new PcmChunker(chunkFrames: 480);
        var got = new List<(int len, long pts)>();
        // 25 ms de son reçus à t = 100 ms : le dernier échantillon date de 100 ms
        ch.Push(new byte[1200 * 4], 100_000, (m, pts) => got.Add((m.Length, pts)));
        Assert.Equal(2, got.Count);
        Assert.All(got, g => Assert.Equal(1920, g.len));
        Assert.Equal(75_000, got[0].pts);
        Assert.Equal(85_000, got[1].pts);
        Assert.Equal(240, ch.PendingFrames);
        // 5 ms de plus à t = 105 ms : un bloc commençant à 95 ms
        ch.Push(new byte[240 * 4], 105_000, (m, pts) => got.Add((m.Length, pts)));
        Assert.Equal(3, got.Count);
        Assert.Equal(95_000, got[2].pts);
    }

    [Fact]
    public void Chunker_TimestampsNeverGoBackwards()
    {
        var ch = new PcmChunker(chunkFrames: 480);
        var pts = new List<long>();
        ch.Push(new byte[480 * 4], 50_000, (_, p) => pts.Add(p));
        // horloge en retard (gigue) : la date calculée serait 35 ms, avant 40 ms + 10 ms
        ch.Push(new byte[480 * 4], 45_000, (_, p) => pts.Add(p));
        Assert.Equal(40_000, pts[0]);
        Assert.Equal(50_000, pts[1]);
    }
}
