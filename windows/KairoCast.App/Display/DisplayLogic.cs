using System.Text;

namespace KairoCast.App.Display;

/// <summary>Un écran vu par Windows (EnumDisplayDevices + réglages courants). Coordonnées du bureau virtuel.</summary>
public sealed record DisplayInfo(
    string DeviceName,
    string AdapterName,
    string AdapterId,
    string MonitorId,
    bool Attached,
    bool Primary,
    int X = 0,
    int Y = 0,
    int Width = 0,
    int Height = 0);

/// <summary>Mode d'affichage proposé par un écran.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Hz);

/// <summary>
/// Logique pure de l'écran virtuel et du choix de l'écran capturé (sans appel à Windows : testée sous macOS/Linux).
/// Le pilote utilisé est Virtual Display Driver (VirtualDrivers, matériel Root\MttVDD).
/// </summary>
public static class DisplayLogic
{
    /// <summary>Identifiant matériel du pilote Virtual Display Driver.</summary>
    public const string HardwareId = @"Root\MttVDD";

    /// <summary>Paquet winget : application « VDD Control » portable, avec devcon et le pilote signé.</summary>
    public const string WingetId = "VirtualDrivers.Virtual-Display-Driver";

    /// <summary>Dossier de configuration lu par le pilote (valeur par défaut de VDDPATH).</summary>
    public const string ConfigDir = @"C:\VirtualDisplayDriver";

    /// <summary>Le pilote nomme son adaptateur ainsi ; son moniteur a l'identifiant EDID « MTT1337 ».</summary>
    public const string AdapterFriendlyName = "Virtual Display Driver";

    public static string WingetInstallArgs =>
        $"install --id {WingetId} -e --accept-source-agreements --accept-package-agreements --disable-interactivity";

    public static string WingetUninstallCommand => $"winget uninstall --id {WingetId} -e";

    public static bool IsVddHardwareId(string? id) =>
        id != null && (id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase) || id.Equals("MttVDD", StringComparison.OrdinalIgnoreCase));

    /// <summary>Écran fourni par le pilote : adaptateur MttVDD ou nommé « Virtual Display Driver », ou moniteur MTT1337.</summary>
    public static bool IsVirtual(DisplayInfo d) =>
        d.AdapterId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
        || d.AdapterName.Contains(AdapterFriendlyName, StringComparison.OrdinalIgnoreCase)
        || d.AdapterName.Contains("IddSampleDriver", StringComparison.OrdinalIgnoreCase)
        || d.MonitorId.Contains("MTT1337", StringComparison.OrdinalIgnoreCase);

    public static DisplayInfo? FindVirtual(IEnumerable<DisplayInfo> displays) =>
        displays.Where(IsVirtual).OrderByDescending(d => d.Attached).FirstOrDefault();

    /// <summary>Écrans réels allumés (le principal d'abord), parmi lesquels choisir l'écran à recopier.</summary>
    public static IReadOnlyList<DisplayInfo> DuplicateCandidates(IEnumerable<DisplayInfo> displays) =>
        displays.Where(d => d.Attached && !IsVirtual(d))
            .OrderByDescending(d => d.Primary).ThenBy(d => Number(d.DeviceName)).ToList();

    /// <summary>Écran à recopier : celui choisi s'il est toujours là, sinon le principal (null = laisser le système choisir).</summary>
    public static string? DuplicateTarget(IEnumerable<DisplayInfo> displays, string? preferred)
    {
        var list = DuplicateCandidates(displays);
        if (!string.IsNullOrEmpty(preferred))
        {
            var p = list.FirstOrDefault(d => d.DeviceName.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p.DeviceName;
        }
        return list.FirstOrDefault(d => d.Primary)?.DeviceName ?? list.FirstOrDefault()?.DeviceName;
    }

    /// <summary>Place de l'écran virtuel : à droite de l'écran le plus à droite, aligné en haut sur l'écran principal.</summary>
    public static (int X, int Y) ExtendPosition(IEnumerable<DisplayInfo> displays, string? virtualName = null)
    {
        var attached = displays.Where(d => d.Attached && d.Width > 0 && d.DeviceName != virtualName).ToList();
        if (attached.Count == 0) return (0, 0);
        var right = attached.Max(d => d.X + d.Width);
        var primary = attached.FirstOrDefault(d => d.Primary) ?? attached[0];
        return (right, primary.Y);
    }

    /// <summary>
    /// Mode à appliquer à l'écran virtuel : la définition demandée si elle existe (60 Hz de préférence),
    /// sinon la plus grande de même format qui tient dedans, sinon la plus grande qui tient, sinon la plus petite.
    /// </summary>
    public static DisplayMode? PickMode(IEnumerable<DisplayMode> modes, int width, int height)
    {
        var list = modes.Where(m => m.Width > 0 && m.Height > 0).Distinct().ToList();
        if (list.Count == 0) return null;
        // Fréquence la plus proche de 60 Hz (la box affiche au plus 60 i/s ; plus haut ne ferait que charger la capture).
        static int HzScore(DisplayMode m) => -Math.Abs(m.Hz - 60);
        var exact = list.Where(m => m.Width == width && m.Height == height).OrderByDescending(HzScore).ToList();
        if (exact.Count > 0) return exact[0];
        var fits = list.Where(m => m.Width <= width && m.Height <= height).ToList();
        var sameAspect = fits.Where(m => (long)m.Width * height == (long)m.Height * width).ToList();
        var pool = sameAspect.Count > 0 ? sameAspect : fits;
        if (pool.Count > 0)
            return pool.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(HzScore).First();
        return list.OrderBy(m => (long)m.Width * m.Height).ThenByDescending(HzScore).First();
    }

    /// <summary>« Écran 2 (principal) – 1920×1080 ».</summary>
    public static string Label(DisplayInfo d)
    {
        var n = Number(d.DeviceName);
        var sb = new StringBuilder(n > 0 ? $"Écran {n}" : d.DeviceName);
        if (d.Primary) sb.Append(" (principal)");
        if (d.Width > 0) sb.Append($" – {d.Width}×{d.Height}");
        return sb.ToString();
    }

    /// <summary>Numéro de « \\.\DISPLAY12 » (12), 0 si absent.</summary>
    public static int Number(string deviceName)
    {
        var i = deviceName.Length;
        while (i > 0 && char.IsDigit(deviceName[i - 1])) i--;
        return i < deviceName.Length && int.TryParse(deviceName[i..], out var n) ? n : 0;
    }

    /// <summary>Sous-dossier du pilote dans le paquet winget : « x86 » contient en réalité le pilote x64.</summary>
    public static string DriverSubdir(bool arm64) => Path.Combine("SignedDrivers", arm64 ? "ARM64" : "x86", "VDD");

    /// <summary>Dossier du paquet winget parmi ceux trouvés (installation utilisateur ou machine), le plus récent d'abord.</summary>
    public static string? PickPackageDir(IEnumerable<string> dirs) =>
        dirs.Where(d => Path.GetFileName(d.TrimEnd('\\', '/')).StartsWith(WingetId + "_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).LastOrDefault();

    /// <summary>
    /// vdd_settings.xml écrit à l'installation (si absent) : un seul écran virtuel, définitions de la box, 60 Hz,
    /// journal du pilote coupé.
    /// </summary>
    public static string SettingsXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8'?>");
        sb.AppendLine("<!-- Écrit par Kairo Cast : un écran virtuel, activé seulement pendant un partage en mode Étendre. -->");
        sb.AppendLine("<vdd_settings>");
        sb.AppendLine("    <monitors><count>1</count></monitors>");
        sb.AppendLine("    <gpu><friendlyname>default</friendlyname></gpu>");
        sb.AppendLine("    <global><g_refresh_rate>60</g_refresh_rate></global>");
        sb.AppendLine("    <resolutions>");
        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
            sb.AppendLine($"        <resolution><width>{w}</width><height>{h}</height><refresh_rate>60</refresh_rate></resolution>");
        sb.AppendLine("    </resolutions>");
        sb.AppendLine("    <logging><SendLogsThroughPipe>false</SendLogsThroughPipe><logging>false</logging><debuglogging>false</debuglogging></logging>");
        sb.AppendLine("    <colour><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus><ColourFormat>RGB</ColourFormat></colour>");
        sb.AppendLine("    <cursor><HardwareCursor>true</HardwareCursor><CursorMaxX>128</CursorMaxX><CursorMaxY>128</CursorMaxY><AlphaCursorSupport>true</AlphaCursorSupport><XorCursorSupportLevel>2</XorCursorSupportLevel></cursor>");
        sb.AppendLine("    <edid><CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride></edid>");
        sb.AppendLine("</vdd_settings>");
        return sb.ToString();
    }
}
