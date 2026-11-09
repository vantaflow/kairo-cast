using System.Buffers.Binary;
using System.Text;

namespace KairoCast.Core;

/// <summary>Protocole Kairo Link v1 (voir docs/superpowers/specs/2026-11-09-emetteur-windows-design.md).</summary>
public static class LinkProtocol
{
    public const int Port = 7100;
    public const string ServiceType = "_kairocast._tcp.local";
    public const byte Version = 1;

    public const byte VideoFormatType = 1;
    public const byte Video = 2;
    public const byte AudioFormatType = 3;
    public const byte Audio = 4;
    public const byte Ping = 5;
    public const byte Bye = 6;

    public const int RefuseBusy = 1;
    public const int RefuseVersion = 2;

    public sealed record Reply(bool Accepted, int MaxWidth, int MaxHeight, bool Audio, int RefuseReason);

    public static byte[] Hello(string senderName)
    {
        var name = Encoding.UTF8.GetBytes(senderName);
        if (name.Length > 200) name = name[..200];
        var b = new byte[7 + name.Length];
        "KCST"u8.CopyTo(b);
        b[4] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(5), (ushort)name.Length);
        name.CopyTo(b, 7);
        return b;
    }

    public static Reply ReadReply(Stream s)
    {
        var magic = Encoding.ASCII.GetString(ReadExactly(s, 4));
        switch (magic)
        {
            case "KCOK":
                var r = ReadExactly(s, 5);
                return new Reply(true, BinaryPrimitives.ReadUInt16BigEndian(r), BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2)), r[4] == 1, 0);
            case "KCNO":
                return new Reply(false, 0, 0, false, ReadExactly(s, 1)[0]);
            default:
                throw new InvalidDataException("Ce n'est pas un récepteur Kairo Cast.");
        }
    }

    public static void WritePacket(Stream s, byte type, long ptsUs, ReadOnlySpan<byte> payload)
    {
        Span<byte> h = stackalloc byte[13];
        h[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(h[1..], payload.Length);
        BinaryPrimitives.WriteInt64BigEndian(h[5..], ptsUs);
        s.Write(h);
        s.Write(payload);
    }

    public static byte[] VideoFormat(int width, int height)
    {
        var b = new byte[24];
        int[] v = { width, height, 0, 0, width, height };
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    public static byte[] AudioFormat(int rate, int channels)
    {
        var b = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(b, rate);
        b[4] = (byte)channels;
        return b;
    }

    static byte[] ReadExactly(Stream s, int n)
    {
        var b = new byte[n];
        s.ReadExactly(b);
        return b;
    }
}
