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

    /// <summary>
    /// Mode à appliquer à l'écran virtuel étendu : la définition demandée si elle existe (60 Hz de préférence),
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

    // ---------------------------------------------------------------- chemins d'affichage (API CCD)

    /// <summary>Code « désactivé » du Gestionnaire de périphériques (CM_PROB_DISABLED).</summary>
    public const int ProblemDisabled = 22;

    /// <summary>Clé d'une source ou d'une cible : « LUID de l'adaptateur:identifiant ».</summary>
    public static string Key(string adapter, uint id) => $"{adapter}:{id}";

    private static string AdapterOf(string key)
    {
        var i = key.LastIndexOf(':');
        return i < 0 ? key : key[..i];
    }

    /// <summary>
    /// Chemin à allumer pour l'écran virtuel parmi tous les chemins possibles (QDC_ALL_PATHS).
    /// Recopie (<paramref name="cloneSource"/> renseigné) : chemin vers l'écran virtuel depuis cette source, qui peut déjà
    /// alimenter plusieurs écrans (un clone HDMI reste un clone, l'écran virtuel s'y ajoute).
    /// Extension : chemin depuis une source qu'aucun autre écran n'utilise, celle de l'adaptateur du pilote de préférence.
    /// </summary>
    public static int? ChooseVirtualPath(IEnumerable<PathDesc> all, IEnumerable<string> usedSources, string? cloneSource)
    {
        var virtuals = all.Where(p => p.Virtual).ToList();
        if (cloneSource != null)
            return virtuals.FirstOrDefault(p => p.SourceKey == cloneSource)?.Index;
        var used = usedSources.ToHashSet();
        var free = virtuals.Where(p => !used.Contains(p.SourceKey)).ToList();
        var own = free.FirstOrDefault(p => AdapterOf(p.SourceKey) == AdapterOf(p.TargetKey));
        return (own ?? free.FirstOrDefault())?.Index;
    }

    /// <summary>Source recopiée par l'écran virtuel : celle de l'écran choisi, sinon celle de l'écran principal.</summary>
    public static string? CloneSource(IEnumerable<PathDesc> active, string? preferredScreen)
    {
        var real = active.Where(p => p.Active && !p.Virtual).ToList();
        if (!string.IsNullOrEmpty(preferredScreen))
        {
            var p = real.FirstOrDefault(r => string.Equals(r.SourceName, preferredScreen, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p.SourceKey;
        }
        return (real.FirstOrDefault(r => r.Primary) ?? real.FirstOrDefault())?.SourceKey;
    }

    /// <summary>Ce que montre la TV d'après les chemins actifs (ce que Win+P a choisi).</summary>
    public static TvLayout LayoutOf(IEnumerable<PathDesc> active)
    {
        var list = active.Where(p => p.Active).ToList();
        var v = list.FirstOrDefault(p => p.Virtual);
        if (v == null) return TvLayout.Off;
        var others = list.Where(p => !p.Virtual).ToList();
        if (others.Count == 0) return TvLayout.Only;
        return others.Any(o => o.SourceKey == v.SourceKey) ? TvLayout.Duplicate : TvLayout.Extend;
    }

    /// <summary>
    /// Correspondance des LUID d'adaptateurs sauvegardés vers les LUID actuels, par chemin de périphérique (un LUID change
    /// à chaque démarrage de Windows ; la sauvegarde peut dater d'avant un redémarrage).
    /// </summary>
    public static Dictionary<string, string> RemapAdapters(IEnumerable<(string Luid, string Path)> saved, IEnumerable<(string Luid, string Path)> current)
    {
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (luid, path) in current)
            if (!string.IsNullOrEmpty(path)) byPath.TryAdd(path, luid);
        var map = new Dictionary<string, string>();
        foreach (var (luid, path) in saved)
            if (!string.IsNullOrEmpty(path) && byPath.TryGetValue(path, out var now) && now != luid)
                map[luid] = now;
        return map;
    }

    // ---------------------------------------------------------------- fenêtres à rapatrier

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// Fenêtres à ramener sur l'écran principal à la fin du partage : visibles, non masquées, ni bureau (Progman,
    /// WorkerW), ni barre des tâches, ni fenêtre d'outils, et dont le centre est sur l'écran virtuel.
    /// </summary>
    public static IReadOnlyList<WindowDesc> WindowsToRescue(IEnumerable<WindowDesc> windows, DisplayRect virtualBounds) =>
        windows.Where(w => w.Visible && !w.Cloaked && !w.ToolWindow && !ShellClasses.Contains(w.Class)
                           && w.Bounds.Width > 0 && w.Bounds.Height > 0 && virtualBounds.Contains(w.Bounds.CenterX, w.Bounds.CenterY))
            .ToList();

    /// <summary>Même place relative, de l'écran virtuel vers la zone de travail de l'écran principal ; taille réduite si besoin.</summary>
    public static DisplayRect Relocate(DisplayRect window, DisplayRect from, DisplayRect to)
    {
        var w = Math.Min(window.Width, to.Width);
        var h = Math.Min(window.Height, to.Height);
        var x = to.X + Math.Clamp(window.X - from.X, 0, to.Width - w);
        var y = to.Y + Math.Clamp(window.Y - from.Y, 0, to.Height - h);
        return new DisplayRect(x, y, w, h);
    }

    // ---------------------------------------------------------------- allumage sans élévation (tâches planifiées)

    /// <summary>Tâche planifiée qui active (true) ou désactive l'écran virtuel.</summary>
    public static string TaskName(bool enable) => enable ? @"\KairoCast\VirtualDisplayOn" : @"\KairoCast\VirtualDisplayOff";

    public static string PnputilArgs(bool enable, string instanceId) =>
        $"{(enable ? "/enable-device" : "/disable-device")} \"{instanceId}\"";

    /// <summary>
    /// Définition de la tâche (créée une fois, avec l'accord de l'utilisateur) : pnputil de System32 avec des arguments
    /// fixes, au nom de l'utilisateur, avec ses droits les plus élevés. L'appli la lance sans élévation (schtasks /run) ;
    /// elle ne peut rien faire d'autre qu'activer ou désactiver ce périphérique précis.
    /// </summary>
    public static string TaskXml(bool enable, string instanceId, string userSid, string pnputil)
    {
        static string E(string v) => System.Security.SecurityElement.Escape(v) ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.AppendLine($"  <RegistrationInfo><Description>Kairo Cast : {(enable ? "active" : "désactive")} l'écran virtuel (Virtual Display Driver) au début ou à la fin d'un partage.</Description></RegistrationInfo>");
        sb.AppendLine("  <Principals><Principal id=\"Author\">");
        sb.AppendLine($"    <UserId>{E(userSid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>");
        sb.AppendLine("  </Principal></Principals>");
        sb.AppendLine("  <Settings>");
        sb.AppendLine("    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>");
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand><AllowHardTerminate>true</AllowHardTerminate>");
        sb.AppendLine("    <Hidden>true</Hidden><ExecutionTimeLimit>PT1M</ExecutionTimeLimit><Priority>5</Priority>");
        sb.AppendLine("  </Settings>");
        sb.AppendLine("  <Actions Context=\"Author\"><Exec>");
        sb.AppendLine($"    <Command>{E(pnputil)}</Command>");
        sb.AppendLine($"    <Arguments>{E(PnputilArgs(enable, instanceId))}</Arguments>");
        sb.AppendLine("  </Exec></Actions>");
        sb.AppendLine("</Task>");
        return sb.ToString();
    }
}

/// <summary>Ce que la TV affiche quand l'écran virtuel est là : rien, la recopie d'un écran, un écran à part, ou seule.</summary>
public enum TvLayout { Off, Duplicate, Extend, Only }

/// <summary>Un chemin source → cible de l'API CCD, réduit à ce que la logique utilise.</summary>
public sealed record PathDesc(int Index, string SourceKey, string TargetKey, bool Active, bool Virtual, string? SourceName = null, bool Primary = false);

/// <summary>Rectangle en coordonnées du bureau.</summary>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Fenêtre de premier niveau vue par EnumWindows.</summary>
public sealed record WindowDesc(long Handle, string Class, bool Visible, bool ToolWindow, bool Cloaked, DisplayRect Bounds, string Title = "");
