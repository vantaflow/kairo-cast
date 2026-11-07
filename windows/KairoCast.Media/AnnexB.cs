namespace KairoCast.Media;

/// <summary>Une unité NAL dans un flux Annex-B. Offset/Length incluent le code de début.</summary>
public readonly record struct NalUnit(int Offset, int Length, int PayloadOffset, int Type);

public static class AnnexB
{
    public const int NalSlice = 1, NalIdr = 5, NalSei = 6, NalSps = 7, NalPps = 8, NalAud = 9;

    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    /// <summary>Découpe un tampon Annex-B (codes de début sur 3 ou 4 octets) en unités NAL.</summary>
    public static List<NalUnit> SplitNals(ReadOnlySpan<byte> data)
    {
        var starts = new List<(int start, int payload)>();
        var i = 0;
        while (i + 3 <= data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    var s = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                    starts.Add((s, i + 3));
                    i += 3;
                    continue;
                }
            }
            i++;
        }
        var result = new List<NalUnit>(starts.Count);
        for (var k = 0; k < starts.Count; k++)
        {
            var (s, p) = starts[k];
            var end = k + 1 < starts.Count ? starts[k + 1].start : data.Length;
            if (p >= end) continue; // NAL vide
            result.Add(new NalUnit(s, end - s, p, data[p] & 0x1F));
        }
        return result;
    }

    public static bool HasStartCode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    public static bool IsIdr(ReadOnlySpan<byte> au)
    {
        foreach (var n in SplitNals(au)) if (n.Type == NalIdr) return true;
        return false;
    }

    /// <summary>Liste des types NAL, pour le journal (ex. « 9,7,8,5 »).</summary>
    public static string Describe(ReadOnlySpan<byte> au) => string.Join(",", SplitNals(au).Select(n => n.Type));

    /// <summary>
    /// Convertit un tampon à longueurs préfixées (AVCC, 4 octets big-endian) en Annex-B.
    /// Renvoie null si le tampon n'est pas cohérent.
    /// </summary>
    public static byte[]? FromLengthPrefixed(ReadOnlySpan<byte> data)
    {
        var outp = new List<byte>(data.Length + 16);
        var i = 0;
        while (i < data.Length)
        {
            if (i + 4 > data.Length) return null;
            var len = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
            i += 4;
            if (len <= 0 || len > data.Length - i) return null;
            outp.AddRange(StartCode);
            outp.AddRange(data.Slice(i, len).ToArray());
            i += len;
        }
        return outp.Count > 0 ? outp.ToArray() : null;
    }
}

/// <summary>
/// Garde les derniers SPS/PPS vus et garantit qu'ils précèdent chaque IDR (exigence du récepteur Kairo Link).
/// </summary>
public sealed class ParameterSetCache
{
    private byte[]? _sps;
    private byte[]? _pps;

    public bool HasParameterSets => _sps != null && _pps != null;

    /// <summary>Charge les SPS/PPS d'un en-tête de séquence Annex-B (MF_MT_MPEG_SEQUENCE_HEADER).</summary>
    public void LoadSequenceHeader(ReadOnlySpan<byte> header) => Observe(header, AnnexB.SplitNals(header));

    /// <summary>
    /// Renvoie l'unité d'accès à envoyer : identique, ou précédée des SPS/PPS en cache si c'est une IDR qui ne les porte pas.
    /// </summary>
    public byte[] Prepare(ReadOnlySpan<byte> au, out bool isKey)
    {
        var nals = AnnexB.SplitNals(au);
        Observe(au, nals);
        isKey = nals.Any(n => n.Type == AnnexB.NalIdr);
        var hasSps = nals.Any(n => n.Type == AnnexB.NalSps);
        var hasPps = nals.Any(n => n.Type == AnnexB.NalPps);
        if (!isKey || (hasSps && hasPps) || !HasParameterSets) return au.ToArray();

        // Insertion après un éventuel délimiteur d'unité d'accès (il doit rester en tête).
        var insertAt = nals.Count > 0 && nals[0].Type == AnnexB.NalAud ? nals[0].Offset + nals[0].Length : 0;
        var extra = (hasSps ? 0 : _sps!.Length) + (hasPps ? 0 : _pps!.Length);
        var outp = new byte[au.Length + extra];
        au[..insertAt].CopyTo(outp);
        var pos = insertAt;
        if (!hasSps) { _sps!.CopyTo(outp, pos); pos += _sps.Length; }
        if (!hasPps) { _pps!.CopyTo(outp, pos); pos += _pps.Length; }
        au[insertAt..].CopyTo(outp.AsSpan(pos));
        return outp;
    }

    private void Observe(ReadOnlySpan<byte> data, List<NalUnit> nals)
    {
        foreach (var n in nals)
        {
            if (n.Type != AnnexB.NalSps && n.Type != AnnexB.NalPps) continue;
            // Toujours stocké avec un code de début sur 4 octets.
            var payload = data.Slice(n.PayloadOffset, n.Offset + n.Length - n.PayloadOffset);
            var copy = new byte[payload.Length + 4];
            copy[3] = 1;
            payload.CopyTo(copy.AsSpan(4));
            if (n.Type == AnnexB.NalSps) _sps = copy; else _pps = copy;
        }
    }
}
