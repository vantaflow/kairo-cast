using System.Net;
using System.Text;

namespace KairoCast.Link;

/// <summary>Enregistrement DNS décodé (sous-ensemble utile à mDNS : PTR, SRV, TXT, A, AAAA).</summary>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl, string? Target, int Port, IPAddress? Address, IReadOnlyDictionary<string, string>? Txt)
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33;
}

/// <summary>Décodeur minimal de messages DNS (avec compression de noms).</summary>
public static class DnsMessage
{
    public static IReadOnlyList<DnsRecord> ParseRecords(ReadOnlySpan<byte> m)
    {
        var list = new List<DnsRecord>();
        if (m.Length < 12) return list;
        int qd = U16(m, 4), an = U16(m, 6), ns = U16(m, 8), ar = U16(m, 10);
        int pos = 12;
        try
        {
            for (int i = 0; i < qd; i++)
            {
                ReadName(m, ref pos);
                pos += 4;
            }
            for (int i = 0; i < an + ns + ar; i++)
            {
                var name = ReadName(m, ref pos);
                if (pos + 10 > m.Length) break;
                ushort type = (ushort)U16(m, pos);
                uint ttl = (uint)((U16(m, pos + 4) << 16) | U16(m, pos + 6));
                int len = U16(m, pos + 8);
                pos += 10;
                int end = pos + len;
                if (end > m.Length) break;
                int p = pos;
                switch (type)
                {
                    case DnsRecord.TypePtr:
                        list.Add(new DnsRecord(name, type, ttl, ReadName(m, ref p), 0, null, null));
                        break;
                    case DnsRecord.TypeSrv when len >= 7:
                        int port = U16(m, pos + 4);
                        p = pos + 6;
                        list.Add(new DnsRecord(name, type, ttl, ReadName(m, ref p), port, null, null));
                        break;
                    case DnsRecord.TypeTxt:
                        list.Add(new DnsRecord(name, type, ttl, null, 0, null, ParseTxt(m[pos..end])));
                        break;
                    case DnsRecord.TypeA when len == 4:
                        list.Add(new DnsRecord(name, type, ttl, null, 0, new IPAddress(m.Slice(pos, 4)), null));
                        break;
                    case DnsRecord.TypeAaaa when len == 16:
                        list.Add(new DnsRecord(name, type, ttl, null, 0, new IPAddress(m.Slice(pos, 16)), null));
                        break;
                }
                pos = end;
            }
        }
        catch (FormatException) { }
        return list;
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> d)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < d.Length)
        {
            int l = d[i++];
            if (i + l > d.Length) break;
            var s = Encoding.UTF8.GetString(d.Slice(i, l));
            i += l;
            int eq = s.IndexOf('=');
            if (eq < 0) { if (s.Length > 0) dict.TryAdd(s, ""); }
            else if (eq > 0) dict.TryAdd(s[..eq], s[(eq + 1)..]);
        }
        return dict;
    }

    private static int U16(ReadOnlySpan<byte> m, int p)
    {
        if (p + 2 > m.Length) throw new FormatException("message tronqué");
        return (m[p] << 8) | m[p + 1];
    }

    private static string ReadName(ReadOnlySpan<byte> m, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos;
        bool jumped = false;
        int hops = 0;
        while (true)
        {
            if (p >= m.Length) throw new FormatException("nom tronqué");
            int l = m[p];
            if (l == 0) { p++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (p + 1 >= m.Length || ++hops > 32) throw new FormatException("compression invalide");
                int target = ((l & 0x3F) << 8) | m[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            if (p + 1 + l > m.Length) throw new FormatException("label tronqué");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(m.Slice(p + 1, l)));
            p += 1 + l;
        }
        if (!jumped) pos = p;
        return sb.ToString();
    }

    /// <summary>Construit une question mDNS (classe IN) pour un ou plusieurs (nom, type).</summary>
    public static byte[] BuildQuery(params (string Name, ushort Type)[] questions)
    {
        var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(0); W16(0); W16(questions.Length); W16(0); W16(0); W16(0);
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)b.Length);
                ms.Write(b);
            }
            ms.WriteByte(0);
            W16(type); W16(1);
        }
        return ms.ToArray();
    }
}
