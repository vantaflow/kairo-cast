namespace KairoCast.App.Capture;

/// <summary>Écran à capturer : null = écran principal ; Virtual = écran virtuel du mode Étendre (pas de repli possible).</summary>
public sealed record CaptureTarget(string? DeviceName, bool Virtual)
{
    public static CaptureTarget Primary { get; } = new(null, false);

    public override string ToString() => DeviceName == null ? "principal" : Virtual ? $"virtuel {DeviceName}" : DeviceName;
}
