using System.Buffers.Binary;

namespace KairoCast.Link;

public static class LinkCodec
{
    public const int HeaderSize = 20;
    public const int MaxPayload = 8 << 20;
    private static readonly byte[] Magic = "KCST"u8.ToArray();

    public static byte[] Encode(LinkFrame f) => Encode(f.Type, f.Flags, f.PtsUs, f.Payload);

    public static byte[] Encode(LinkType type, int flags, long ptsUs, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new LinkProtocolException($"charge trop grande {payload.Length}");
        var b = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(b, 0);
        b[4] = (byte)type;
        b[5] = (byte)flags;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(8), ptsUs);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)payload.Length);
        payload.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    /// <summary>null = fin de flux propre avant une trame.</summary>
    public static async Task<LinkFrame?> ReadAsync(Stream s, CancellationToken ct)
    {
        var h = new byte[HeaderSize];
        if (await ReadFullyAsync(s, h, true, ct) == 0) return null;
        if (!h.AsSpan(0, 4).SequenceEqual(Magic)) throw new LinkProtocolException("magic invalide");
        var code = h[4];
        if (!Enum.IsDefined(typeof(LinkType), code)) throw new LinkProtocolException($"type inconnu {code}");
        var len = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(16));
        if (len > MaxPayload) throw new LinkProtocolException($"charge trop grande {len}");
        var payload = new byte[len];
        if (len > 0) await ReadFullyAsync(s, payload, false, ct);
        return new LinkFrame((LinkType)code, h[5], BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(8)), payload);
    }

    private static async Task<int> ReadFullyAsync(Stream s, byte[] buf, bool allowEmpty, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(off), ct);
            if (r == 0)
            {
                if (off == 0 && allowEmpty) return 0;
                throw new LinkProtocolException("flux tronque");
            }
            off += r;
        }
        return off;
    }
}
