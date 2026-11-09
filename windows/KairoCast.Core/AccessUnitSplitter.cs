namespace KairoCast.Core;

/// <summary>
/// Découpe un flux H.264 Annex B (sortie de ffmpeg avec délimiteurs AUD) en unités d'accès :
/// chaque unité commence à un délimiteur (NAL de type 9) et s'arrête au suivant.
/// </summary>
public sealed class AccessUnitSplitter
{
    readonly List<byte> buf = new(1 << 20);
    int scanFrom; // position à partir de laquelle chercher le prochain délimiteur

    public IEnumerable<byte[]> Push(ReadOnlySpan<byte> data)
    {
        var units = new List<byte[]>();
        foreach (var x in data) buf.Add(x);
        while (true)
        {
            int first = FindAud(0);
            if (first < 0) { scanFrom = Math.Max(0, buf.Count - 4); break; }
            if (first > 0) { buf.RemoveRange(0, first); scanFrom = 0; continue; } // octets avant le premier AUD
            int next = FindAud(Math.Max(scanFrom, 4));
            if (next < 0) { scanFrom = Math.Max(4, buf.Count - 4); break; }
            units.Add(buf.GetRange(0, next).ToArray());
            buf.RemoveRange(0, next);
            scanFrom = 0;
        }
        return units;
    }

    /// <summary>Dernière unité en attente (fin de flux), ou null.</summary>
    public byte[]? Flush()
    {
        if (buf.Count == 0) return null;
        var u = buf.ToArray();
        buf.Clear();
        scanFrom = 0;
        return u;
    }

    /// <summary>Début (code de démarrage compris, zéro de tête inclus) du premier AUD à partir de <paramref name="from"/>.</summary>
    int FindAud(int from)
    {
        for (int i = from; i + 3 < buf.Count; i++)
        {
            if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1 && (buf[i + 3] & 0x1F) == 9)
                return i > 0 && buf[i - 1] == 0 && i - 1 >= from - 1 ? i - 1 : i;
        }
        return -1;
    }

    public static bool IsKeyFrame(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1 && (au[i + 3] & 0x1F) == 5) return true;
        return false;
    }
}
