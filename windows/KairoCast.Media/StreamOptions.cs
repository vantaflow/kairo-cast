namespace KairoCast.Media;

/// <summary>Réglages d'une session de partage (1080p30, 8 Mb/s et son par défaut).</summary>
public sealed record StreamOptions(int Width = 1920, int Height = 1080, int Fps = 30, int BitrateKbps = 8000, bool Audio = true)
{
    /// <summary>Copie normalisée : dimensions paires, i/s entre 1 et 120, débit positif.</summary>
    public StreamOptions Validated()
    {
        var (w, h) = VideoSizing.Even(Math.Clamp(Width, 2, 4096), Math.Clamp(Height, 2, 4096));
        var fps = Fps <= 0 ? 30 : Math.Min(Fps, 120);
        var kbps = BitrateKbps > 0 ? Math.Clamp(BitrateKbps, 200, 100_000) : QualityPresets.SuggestKbps(w, h, fps);
        return this with { Width = w, Height = h, Fps = fps, BitrateKbps = kbps };
    }
}

public enum QualityPreset { P1080F30, P1080F60, P1440F30 }

public static class QualityPresets
{
    public static StreamOptions Default { get; } = Get(QualityPreset.P1080F30);

    public static StreamOptions Get(QualityPreset p, bool audio = true) => p switch
    {
        QualityPreset.P1080F60 => new StreamOptions(1920, 1080, 60, SuggestKbps(1920, 1080, 60), audio),
        QualityPreset.P1440F30 => new StreamOptions(2560, 1440, 30, SuggestKbps(2560, 1440, 30), audio),
        _ => new StreamOptions(1920, 1080, 30, 8000, audio),
    };

    /// <summary>Débit conseillé : 8 Mb/s pour 1080p30, proportionnel aux pixels, +50 % pour 60 i/s, minimum 1 Mb/s.</summary>
    public static int SuggestKbps(int width, int height, int fps)
    {
        var pixels = (double)width * height / (1920.0 * 1080.0);
        var fpsFactor = fps <= 30 ? fps / 30.0 : 1.0 + (fps - 30) / 60.0;
        return Math.Max(1000, (int)Math.Round(8000 * pixels * fpsFactor));
    }
}
