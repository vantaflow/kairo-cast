using System.Net;
using System.Text;
using KairoCast.Link;

namespace KairoCast.Tests;

public class DiscoveryTests
{
    private const string Svc = "_kairocast._tcp.local";
    private const string Inst = "Kairo Link._kairocast._tcp.local";
    private const string Host = "Android_73a1.local";

    // Constructeur de messages DNS (réponses) avec compression pour exercer le décodeur.
    private sealed class Msg
    {
        private readonly MemoryStream _ms = new();
        private readonly Dictionary<string, int> _names = new();
        private int _count;
        public Msg() { for (int i = 0; i < 12; i++) _ms.WriteByte(0); _ms.Position = 2; W16(0x8400); _ms.Position = 12; }
        private void W16(int v) { _ms.WriteByte((byte)(v >> 8)); _ms.WriteByte((byte)v); }
        private void Name(string n)
        {
            var labels = n.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                var suffix = string.Join('.', labels[i..]);
                if (_names.TryGetValue(suffix, out var off)) { W16(0xC000 | off); return; }
                _names[suffix] = (int)_ms.Position;
                var b = Encoding.UTF8.GetBytes(labels[i]);
                _ms.WriteByte((byte)b.Length); _ms.Write(b);
            }
            _ms.WriteByte(0);
        }
        private Msg Rec(string name, int type, uint ttl, Action body)
        {
            Name(name); W16(type); W16(0x8001); W16((int)(ttl >> 16)); W16((int)(ttl & 0xFFFF));
            long lenPos = _ms.Position; W16(0);
            body();
            long end = _ms.Position;
            _ms.Position = lenPos; W16((int)(end - lenPos - 2)); _ms.Position = end;
            _count++;
            return this;
        }
        public Msg Ptr(string name, string target, uint ttl = 4500) => Rec(name, 12, ttl, () => Name(target));
        public Msg Srv(string name, string target, int port, uint ttl = 120) => Rec(name, 33, ttl, () => { W16(0); W16(0); W16(port); Name(target); });
        public Msg Txt(string name, params string[] kv) => Rec(name, 16, 4500, () =>
        {
            foreach (var s in kv) { var b = Encoding.UTF8.GetBytes(s); _ms.WriteByte((byte)b.Length); _ms.Write(b); }
        });
        public Msg A(string name, string ip, uint ttl = 120) => Rec(name, 1, ttl, () => _ms.Write(IPAddress.Parse(ip).GetAddressBytes()));
        public byte[] Build()
        {
            var a = _ms.ToArray();
            a[6] = (byte)(_count >> 8); a[7] = (byte)_count;
            return a;
        }
    }

    private static byte[] FullAnswer(uint ptrTtl = 4500, string name = "Kairo") =>
        new Msg().Ptr(Svc, Inst, ptrTtl).Srv(Inst, Host, 7300).Txt(Inst, "v=1", "name=" + name).A(Host, "192.168.0.62").Build();

    private static (BoxTracker T, List<BoxInfo> Found, List<BoxInfo> Lost) Make()
    {
        var t = new BoxTracker(Svc);
        var f = new List<BoxInfo>(); var l = new List<BoxInfo>();
        t.Found += f.Add; t.Lost += l.Add;
        return (t, f, l);
    }

    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ParseRecords_decodes_ptr_srv_txt_a()
    {
        var recs = DnsMessage.ParseRecords(FullAnswer());
        Assert.Equal(4, recs.Count);
        Assert.Equal(Inst, recs[0].Target);
        Assert.Equal(7300, recs[1].Port);
        Assert.Equal(Host, recs[1].Target);
        Assert.Equal("Kairo", recs[2].Txt!["name"]);
        Assert.Equal(IPAddress.Parse("192.168.0.62"), recs[3].Address);
    }

    [Fact]
    public void Found_with_name_from_txt()
    {
        var (t, found, _) = Make();
        t.Feed(DnsMessage.ParseRecords(FullAnswer(name: "Salon")), T0);
        var b = Assert.Single(found);
        Assert.Equal("Salon", b.Name);
        Assert.Equal(IPAddress.Parse("192.168.0.62"), b.Address);
        Assert.Equal(7300, b.Port);
    }

    [Fact]
    public void Name_falls_back_to_instance_label()
    {
        var (t, found, _) = Make();
        t.Feed(DnsMessage.ParseRecords(new Msg().Ptr(Svc, Inst).Srv(Inst, Host, 7300).A(Host, "192.168.0.62").Build()), T0);
        Assert.Equal("Kairo Link", Assert.Single(found).Name);
    }

    [Fact]
    public void Found_not_fired_twice()
    {
        var (t, found, _) = Make();
        t.Feed(DnsMessage.ParseRecords(FullAnswer()), T0);
        t.Feed(DnsMessage.ParseRecords(FullAnswer()), T0.AddSeconds(10));
        Assert.Single(found);
    }

    [Fact]
    public void Found_waits_until_resolved_across_messages()
    {
        var (t, found, _) = Make();
        t.Feed(DnsMessage.ParseRecords(new Msg().Ptr(Svc, Inst).Build()), T0);
        Assert.Empty(found);
        Assert.Single(t.Unresolved);
        t.Feed(DnsMessage.ParseRecords(new Msg().Srv(Inst, Host, 7300).Build()), T0);
        Assert.Empty(found);
        t.Feed(DnsMessage.ParseRecords(new Msg().A(Host, "192.168.0.62").Build()), T0);
        Assert.Single(found);
    }

    [Fact]
    public void Lost_on_goodbye()
    {
        var (t, found, lost) = Make();
        t.Feed(DnsMessage.ParseRecords(FullAnswer()), T0);
        t.Feed(DnsMessage.ParseRecords(new Msg().Ptr(Svc, Inst, 0).Build()), T0.AddSeconds(5));
        Assert.Equal(found[0], Assert.Single(lost));
    }

    [Fact]
    public void Lost_on_ttl_expiry_and_refound_after()
    {
        var (t, found, lost) = Make();
        t.Feed(DnsMessage.ParseRecords(FullAnswer(ptrTtl: 100)), T0);
        t.Sweep(T0.AddSeconds(50));
        Assert.Empty(lost);
        t.Sweep(T0.AddSeconds(101));
        Assert.Single(lost);
        t.Feed(DnsMessage.ParseRecords(FullAnswer()), T0.AddSeconds(200));
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Garbage_is_ignored()
    {
        Assert.Empty(DnsMessage.ParseRecords(new byte[] { 1, 2, 3 }));
        var bad = FullAnswer(); 
        Assert.NotNull(DnsMessage.ParseRecords(bad.AsSpan(0, 40)));
    }
}
