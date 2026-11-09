using System.Buffers.Binary;
using System.Net;
using System.Text;
using KairoCast.Core;

namespace KairoCast.Core.Tests;

public class LinkProtocolTests
{
    [Fact]
    public void HelloIsMagicVersionAndName()
    {
        var b = LinkProtocol.Hello("PC");
        Assert.Equal("KCST"u8.ToArray().Concat(new byte[] { 1, 0, 2, (byte)'P', (byte)'C' }), b);
    }

    [Fact]
    public void ParsesAccept()
    {
        var ms = new MemoryStream("KCOK"u8.ToArray().Concat(new byte[] { 0x0A, 0x00, 0x05, 0xA0, 1 }).ToArray());
        var r = LinkProtocol.ReadReply(ms);
        Assert.True(r.Accepted);
        Assert.Equal(2560, r.MaxWidth);
        Assert.Equal(1440, r.MaxHeight);
        Assert.True(r.Audio);
    }

    [Fact]
    public void ParsesBusy()
    {
        var r = LinkProtocol.ReadReply(new MemoryStream("KCNO"u8.ToArray().Append((byte)1).ToArray()));
        Assert.False(r.Accepted);
        Assert.Equal(LinkProtocol.RefuseBusy, r.RefuseReason);
    }

    [Fact]
    public void ThrowsOnUnknownReply()
    {
        Assert.Throws<InvalidDataException>(() => LinkProtocol.ReadReply(new MemoryStream("HTTP/1.1"u8.ToArray())));
    }

    [Fact]
    public void WritesPacketHeader()
    {
        var ms = new MemoryStream();
        LinkProtocol.WritePacket(ms, LinkProtocol.Video, 16_666, new byte[] { 9, 8 });
        var b = ms.ToArray();
        Assert.Equal(15, b.Length);
        Assert.Equal(LinkProtocol.Video, b[0]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(1)));
        Assert.Equal(16_666, BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(5)));
        Assert.Equal(new byte[] { 9, 8 }, b[13..]);
    }

    [Fact]
    public void FormatPayloads()
    {
        var v = LinkProtocol.VideoFormat(1920, 1080);
        Assert.Equal(24, v.Length);
        Assert.Equal(1920, BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(0)));
        Assert.Equal(1080, BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(20)));
        var a = LinkProtocol.AudioFormat(48_000, 2);
        Assert.Equal(new byte[] { 0, 0, 0xBB, 0x80, 2 }, a);
    }
}

public class AccessUnitSplitterTests
{
    static readonly byte[] Aud = { 0, 0, 0, 1, 0x09, 0xF0 };
    static byte[] Nal(byte type, int n = 3) => new byte[] { 0, 0, 0, 1, type }.Concat(Enumerable.Repeat((byte)0xAA, n)).ToArray();

    [Fact]
    public void SplitsOnAccessUnitDelimiters()
    {
        var s = new AccessUnitSplitter();
        var stream = Aud.Concat(Nal(0x67)).Concat(Nal(0x65)).Concat(Aud).Concat(Nal(0x41)).Concat(Aud).ToArray();
        var units = new List<byte[]>();
        // Livré par morceaux de 5 octets, comme un tube.
        for (int i = 0; i < stream.Length; i += 5) units.AddRange(s.Push(stream.AsSpan(i, Math.Min(5, stream.Length - i))));
        Assert.Equal(2, units.Count);
        Assert.Equal(Aud.Concat(Nal(0x67)).Concat(Nal(0x65)).ToArray(), units[0]);
        Assert.True(AccessUnitSplitter.IsKeyFrame(units[0]));
        Assert.False(AccessUnitSplitter.IsKeyFrame(units[1]));
    }

    [Fact]
    public void FlushReturnsLastUnit()
    {
        var s = new AccessUnitSplitter();
        Assert.Empty(s.Push(Aud.Concat(Nal(0x41)).ToArray()));
        Assert.Equal(Aud.Concat(Nal(0x41)).ToArray(), s.Flush());
        Assert.Null(s.Flush());
    }

    [Fact]
    public void ThreeByteStartCodes()
    {
        var s = new AccessUnitSplitter();
        byte[] aud3 = { 0, 0, 1, 0x09, 0xF0 };
        var units = s.Push(aud3.Concat(new byte[] { 0, 0, 1, 0x65, 1 }).Concat(aud3).ToArray()).ToList();
        Assert.Single(units);
        Assert.True(AccessUnitSplitter.IsKeyFrame(units[0]));
    }
}

public class MdnsTests
{
    [Fact]
    public void QueryAsksPtrForService()
    {
        var q = Mdns.BuildQuery("_kairocast._tcp.local");
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4))); // une question
        Assert.Contains("_kairocast", Encoding.ASCII.GetString(q));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 4))); // PTR
    }

    [Fact]
    public void ParsesResponseWithCompression()
    {
        var b = new DnsBuilder();
        b.Header(answers: 1, additional: 2);
        int svc = b.Name("_kairocast._tcp.local");
        b.Rr(12, () => b.NameRef("Salon", svc));               // PTR -> Salon._kairocast._tcp.local
        int inst = b.LastNameOffset;
        b.NamePtr(inst); b.Rr(33, () => { b.U16(0); b.U16(0); b.U16(7100); b.Name("box.local"); });
        int host = b.LastNameOffset;
        b.NamePtr(host); b.Rr(1, () => b.Bytes(192, 168, 0, 62));
        var found = Mdns.Parse(b.ToArray(), IPAddress.Parse("192.168.0.62"));
        var r = Assert.Single(found);
        Assert.Equal("Salon", r.Name);
        Assert.Equal(IPAddress.Parse("192.168.0.62"), r.Address);
        Assert.Equal(7100, r.Port);
    }

    [Fact]
    public void FallsBackToSenderAddressWithoutARecord()
    {
        var b = new DnsBuilder();
        b.Header(answers: 1, additional: 1);
        int svc = b.Name("_kairocast._tcp.local");
        b.Rr(12, () => b.NameRef("Salon", svc));
        int inst = b.LastNameOffset;
        b.NamePtr(inst); b.Rr(33, () => { b.U16(0); b.U16(0); b.U16(7100); b.Name("box.local"); });
        var r = Assert.Single(Mdns.Parse(b.ToArray(), IPAddress.Parse("10.0.0.5")));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), r.Address);
    }

    [Fact]
    public void IgnoresGarbage() => Assert.Empty(Mdns.Parse(new byte[] { 1, 2, 3 }, IPAddress.Loopback));

    /// <summary>Petit constructeur de réponses DNS pour les tests.</summary>
    sealed class DnsBuilder
    {
        readonly List<byte> b = new();
        public int LastNameOffset { get; private set; }
        public void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        public void Bytes(params byte[] v) => b.AddRange(v);
        public void Header(int answers, int additional) { U16(0); U16(0x8400); U16(0); U16(answers); U16(0); U16(additional); }
        public int Name(string name)
        {
            int at = b.Count; LastNameOffset = at;
            foreach (var l in name.Split('.')) { b.Add((byte)l.Length); b.AddRange(Encoding.UTF8.GetBytes(l)); }
            b.Add(0); return at;
        }
        public void NameRef(string label, int suffix)
        {
            LastNameOffset = b.Count;
            b.Add((byte)label.Length); b.AddRange(Encoding.UTF8.GetBytes(label)); U16(0xC000 | suffix);
        }
        public void NamePtr(int at) => U16(0xC000 | at);
        /// <summary>Le nom de l'enregistrement doit déjà être écrit (sauf PTR : nom du service, écrit avant).</summary>
        public void Rr(int type, Action rdata)
        {
            if (type == 12) { /* le nom du service vient d'être écrit par Name() */ }
            U16(type); U16(1); U16(0); U16(120);
            int lenAt = b.Count; U16(0);
            int start = b.Count; rdata(); int len = b.Count - start;
            b[lenAt] = (byte)(len >> 8); b[lenAt + 1] = (byte)len;
        }
        public byte[] ToArray() => b.ToArray();
    }
}

public class MediaTests
{
    [Fact]
    public void FloatToS16ClampsAndScales()
    {
        var f = new float[] { 0f, 1f, -1f, 2f, 0.5f };
        var bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        var s = Pcm.FloatToS16(bytes);
        short At(int i) => BinaryPrimitives.ReadInt16LittleEndian(s.AsSpan(i * 2));
        Assert.Equal(0, At(0)); Assert.Equal(32767, At(1)); Assert.Equal(-32767, At(2));
        Assert.Equal(32767, At(3)); Assert.Equal(16383, At(4));
    }

    [Fact]
    public void ToStereoKeepsFrontPair()
    {
        // 2 trames de 6 canaux (5.1) : valeurs 1..6 puis 11..16.
        var s16 = new byte[24];
        for (int i = 0; i < 12; i++) BinaryPrimitives.WriteInt16LittleEndian(s16.AsSpan(i * 2), (short)(i < 6 ? i + 1 : i + 5));
        var st = Pcm.ToStereo(s16, 6);
        Assert.Equal(8, st.Length);
        Assert.Equal(new short[] { 1, 2, 11, 12 }, Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(st.AsSpan(i * 2))).ToArray());
        Assert.Same(s16, Pcm.ToStereo(s16, 2));
    }

    [Theory]
    [InlineData(3840, 2160, 2560, 1440, 2560, 1440)]
    [InlineData(1920, 1080, 2560, 1440, 1920, 1080)]
    [InlineData(1920, 1200, 1920, 1080, 1728, 1080)]
    [InlineData(1366, 768, 3840, 2160, 1366, 768)]
    [InlineData(1365, 767, 3840, 2160, 1364, 766)]
    public void FitKeepsRatioAndEvenSize(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), Ffmpeg.Fit(sw, sh, mw, mh));
    }

    [Fact]
    public void ArgsUseDesktopDuplicationAndAnnexB()
    {
        var a = Ffmpeg.Arguments("h264_nvenc", output: 1, width: 1920, height: 1080, fps: 60, bitrateKbps: 12000);
        Assert.Contains("ddagrab=output_idx=1:framerate=60", a);
        Assert.Contains("-c:v h264_nvenc", a);
        Assert.Contains("aud=insert", a);
        Assert.Contains("-f h264 -", a);
        Assert.Contains("1920:1080", a);
        Assert.Contains("-bf 0", a);
    }

    [Fact]
    public void EncodersTriedHardwareFirst()
    {
        Assert.Equal(new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf" }, Ffmpeg.Encoders);
    }

    [Theory]
    [InlineData(1920, 1080, 60, 12000)]
    [InlineData(3840, 2160, 60, 30000)]
    [InlineData(1280, 720, 30, 4000)]
    public void BitrateScalesWithPixels(int w, int h, int fps, int expected)
    {
        Assert.Equal(expected, Ffmpeg.Bitrate(w, h, fps));
    }
}
