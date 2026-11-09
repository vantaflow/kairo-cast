using System.Runtime.InteropServices;

namespace KairoCast.App.Display;

/// <summary>
/// API « Connecting and Configuring Displays » (QueryDisplayConfig/SetDisplayConfig) : branche ou retire du bureau
/// la sortie de l'écran virtuel sans toucher aux autres écrans. Sans élévation, comme Paramètres › Écran.
/// Nécessaire parce que Windows peut mettre l'écran virtuel en recopie d'un écran réel (portable : « Dupliquer »
/// par défaut au branchement d'un écran) : l'API GDI ne sait pas sortir un écran d'une recopie.
/// </summary>
internal static class DisplayConfig
{
    private const uint QdcAllPaths = 0x1;
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint SdcApply = 0x80;
    private const uint SdcUseSuppliedDisplayConfig = 0x20;
    private const uint SdcSaveToDatabase = 0x200;
    private const uint SdcAllowChanges = 0x400;
    private const uint PathActive = 0x1;
    private const uint ModeIdxInvalid = 0xFFFFFFFF;
    private const int InfoGetSourceName = 1;
    private const int InfoGetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public LUID AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology, Rotation, Scaling;
        public uint RefreshNumerator, RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo Source; public PathTargetInfo Target; public uint Flags; }

    /// <summary>DISPLAYCONFIG_MODE_INFO : en-tête de 16 octets puis union de 48 octets (non interprétée ici).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo { public uint InfoType; public uint Id; public LUID AdapterId; public ulong U0, U1, U2, U3, U4, U5; }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoHeader { public int Type; public int Size; public LUID AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public InfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public InfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId, EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPaths, PathInfo[]? paths, uint numModes, ModeInfo[]? modes, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);

    private static (PathInfo[] Paths, ModeInfo[] Modes) Query(uint flags)
    {
        for (var attempt = 0; ; attempt++)
        {
            var r = GetDisplayConfigBufferSizes(flags, out var np, out var nm);
            if (r != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes : erreur {r}");
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            r = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r == 122 && attempt < 3) continue; // ERROR_INSUFFICIENT_BUFFER : la configuration a changé entre-temps
            if (r != 0) throw new InvalidOperationException($"QueryDisplayConfig : erreur {r}");
            return (paths[..(int)np], modes[..(int)nm]);
        }
    }

    private static string MonitorPath(in PathInfo p)
    {
        var t = new TargetDeviceName
        {
            Header = new InfoHeader { Type = InfoGetTargetName, Size = Marshal.SizeOf<TargetDeviceName>(), AdapterId = p.Target.AdapterId, Id = p.Target.Id },
            MonitorFriendlyDeviceName = "",
            MonitorDevicePath = "",
        };
        return DisplayConfigGetDeviceInfo(ref t) == 0 ? t.MonitorDevicePath ?? "" : "";
    }

    private static string SourceName(in PathInfo p)
    {
        var s = new SourceDeviceName
        {
            Header = new InfoHeader { Type = InfoGetSourceName, Size = Marshal.SizeOf<SourceDeviceName>(), AdapterId = p.Source.AdapterId, Id = p.Source.Id },
            ViewGdiDeviceName = "",
        };
        return DisplayConfigGetDeviceInfo(ref s) == 0 ? s.ViewGdiDeviceName ?? "" : "";
    }

    private static bool IsVirtualTarget(in PathInfo p) => MonitorPath(p).Contains("MTT1337", StringComparison.OrdinalIgnoreCase);

    private static bool SameAdapter(LUID a, LUID b) => a.LowPart == b.LowPart && a.HighPart == b.HighPart;

    /// <summary>Résumé des chemins actifs, pour le journal.</summary>
    public static string DescribeActive()
    {
        try
        {
            var (paths, _) = Query(QdcOnlyActivePaths);
            return string.Join(" ; ", paths.Select(p => $"{SourceName(p)}→{Short(MonitorPath(p))}"));
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Tous les chemins possibles (diagnostic : KairoCast.exe --displays).</summary>
    public static string DescribeAll()
    {
        try
        {
            var (paths, _) = Query(QdcAllPaths);
            return string.Join("\n", paths.Select(p =>
                $"  {((p.Flags & PathActive) != 0 ? "actif  " : "inactif")} source {p.Source.AdapterId.LowPart:X}/{p.Source.Id} {SourceName(p)} → cible {p.Target.AdapterId.LowPart:X}/{p.Target.Id} {Short(MonitorPath(p))} dispo={p.Target.TargetAvailable}"));
        }
        catch (Exception e) { return e.Message; }
    }

    private static string Short(string monitorPath)
    {
        var parts = monitorPath.Split('#');
        return parts.Length > 1 ? parts[1] : monitorPath;
    }

    /// <summary>
    /// Branche l'écran virtuel en extension (sur sa propre source, hors de toute recopie). Renvoie le nom GDI de sa
    /// source (« \\.\DISPLAY5 »), ou lève une exception avec le code d'erreur de SetDisplayConfig.
    /// </summary>
    public static string Extend()
    {
        for (var attempt = 0; ; attempt++)
        {
            var (paths, modes) = Query(QdcAllPaths);
            var active = paths.Where(p => (p.Flags & PathActive) != 0).ToList();
            // Déjà en extension : écran virtuel actif sur une source qu'il est seul à utiliser.
            foreach (var p in active.Where(p => IsVirtualTarget(p)))
                if (active.Count(q => q.Source.Id == p.Source.Id && SameAdapter(q.Source.AdapterId, p.Source.AdapterId)) == 1)
                    return SourceName(p);

            // Sources déjà utilisées par les écrans réels (hors écran virtuel).
            var kept = active.Where(p => !IsVirtualTarget(p)).ToList();
            bool SourceFree(PathInfo p) => !kept.Any(k => k.Source.Id == p.Source.Id && SameAdapter(k.Source.AdapterId, p.Source.AdapterId));
            var free = paths.Where(p => IsVirtualTarget(p) && SourceFree(p)).ToList();
            // De préférence la source propre au pilote (son adaptateur), sinon une source libre de la carte graphique.
            var candidate = free.Where(p => SameAdapter(p.Source.AdapterId, p.Target.AdapterId)).Cast<PathInfo?>().FirstOrDefault()
                            ?? free.Cast<PathInfo?>().FirstOrDefault();
            if (candidate == null)
            {
                // En recopie, Windows ne propose l'écran virtuel que sur la source recopiée : on l'en retire d'abord,
                // ses propres sources réapparaissent alors.
                if (attempt == 0 && Remove() > 0)
                {
                    Thread.Sleep(300);
                    continue;
                }
                throw new InvalidOperationException("aucune sortie libre pour l'écran virtuel");
            }
            return Apply(candidate.Value, kept, modes);
        }
    }

    private static string Apply(PathInfo path, List<PathInfo> kept, ModeInfo[] modes)
    {
        path.Flags |= PathActive;
        path.Source.ModeInfoIdx = ModeIdxInvalid;
        path.Target.ModeInfoIdx = ModeIdxInvalid;
        var apply = kept.Append(path).ToArray();
        var r = SetDisplayConfig((uint)apply.Length, apply, (uint)modes.Length, modes, SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
        if (r != 0) throw new InvalidOperationException($"SetDisplayConfig (extension) : erreur {r}");
        return SourceName(path);
    }

    /// <summary>Retire l'écran virtuel du bureau (extension ou recopie). Renvoie le nombre de chemins retirés.</summary>
    public static int Remove()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        var kept = paths.Where(p => !IsVirtualTarget(p)).ToArray();
        var removed = paths.Length - kept.Length;
        if (removed == 0) return 0;
        if (kept.Length == 0) throw new InvalidOperationException("l'écran virtuel est le seul écran allumé : il reste en place");
        var r = SetDisplayConfig((uint)kept.Length, kept, (uint)modes.Length, modes, SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
        if (r != 0) throw new InvalidOperationException($"SetDisplayConfig (retrait) : erreur {r}");
        return removed;
    }
}
