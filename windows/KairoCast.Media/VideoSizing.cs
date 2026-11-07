namespace KairoCast.Media;

/// <summary>Rectangle de destination (pixels) de l'image capturée dans l'image encodée.</summary>
public readonly record struct FitRect(int X, int Y, int Width, int Height);

public static class VideoSizing
{
    /// <summary>Arrondit à la dimension paire inférieure (NV12 / H.264 exigent des dimensions paires), au moins 2.</summary>
    public static (int Width, int Height) Even(int width, int height) =>
        (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    /// <summary>
    /// Place une source <paramref name="srcW"/>×<paramref name="srcH"/> dans une cible <paramref name="dstW"/>×<paramref name="dstH"/>
    /// en gardant les proportions, centrée, bandes noires seulement si nécessaire. Toutes les valeurs sont paires.
    /// </summary>
    public static FitRect Fit(int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW <= 0) throw new ArgumentOutOfRangeException(nameof(srcW));
        if (srcH <= 0) throw new ArgumentOutOfRangeException(nameof(srcH));
        if (dstW < 2) throw new ArgumentOutOfRangeException(nameof(dstW));
        if (dstH < 2) throw new ArgumentOutOfRangeException(nameof(dstH));
        (dstW, dstH) = Even(dstW, dstH);

        int w, h;
        // Comparaison en entiers 64 bits : srcW/srcH >= dstW/dstH ?
        if ((long)srcW * dstH >= (long)dstW * srcH)
        {
            w = dstW;
            h = RoundEven((double)srcH * dstW / srcW, dstH);
        }
        else
        {
            h = dstH;
            w = RoundEven((double)srcW * dstH / srcH, dstW);
        }
        var x = ((dstW - w) / 2) & ~1;
        var y = ((dstH - h) / 2) & ~1;
        return new FitRect(x, y, w, h);
    }

    private static int RoundEven(double v, int max)
    {
        var r = (int)Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2;
        return Math.Clamp(r, 2, max);
    }
}
