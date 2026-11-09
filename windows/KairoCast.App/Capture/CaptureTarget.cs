namespace KairoCast.App.Capture;

/// <summary>
/// Écran à capturer. DeviceName null = écran principal. Virtual : écran virtuel (la TV) — pas de repli sur un autre écran,
/// et <see cref="Follow"/> donne l'écran à capturer après un changement d'affichage (Win+P : Dupliquer, Étendre…),
/// null si Windows n'affiche plus la TV.
/// </summary>
public sealed record CaptureTarget(string? DeviceName, bool Virtual, Func<string?>? Follow = null)
{
    public static CaptureTarget Primary { get; } = new(null, false);

    public override string ToString() => DeviceName == null ? "principal" : Virtual ? $"TV (écran virtuel, {DeviceName})" : DeviceName;
}
