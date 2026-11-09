using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KairoCast.App.Display;

/// <summary>
/// Implémentation Windows de <see cref="IDisplaySystem"/> : API « Connecting and Configuring Displays »
/// (QueryDisplayConfig/SetDisplayConfig, sans élévation, comme Paramètres › Écran) et fenêtres (EnumWindows).
/// La logique (quel chemin allumer, quelles fenêtres rapatrier) est dans <see cref="DisplayLogic"/> ; ici, seulement
/// la traduction vers Win32.
/// </summary>
internal sealed class DisplaySystem : IDisplaySystem
{
    private const uint QdcAllPaths = 0x1;
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint SdcUseDatabaseCurrent = 0xF;
    private const uint SdcApply = 0x80;
    private const uint SdcUseSuppliedDisplayConfig = 0x20;
    private const uint SdcSaveToDatabase = 0x200;
    private const uint SdcAllowChanges = 0x400;
    private const uint PathActive = 0x1;
    private const uint ModeIdxInvalid = 0xFFFFFFFF;
    private const uint ModeTypeSource = 1;
    private const int InfoGetSourceName = 1;
    private const int InfoGetTargetName = 2;
    private const int InfoGetAdapterName = 4;

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

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO : en-tête de 16 octets puis union de 48 octets. Mode source : largeur, hauteur,
    /// format, position x, position y (U0 = largeur | hauteur, U1 = format | x, U2 = y).
    /// </summary>
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterName
    {
        public InfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string AdapterDevicePath;
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

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdapterName info);

    // ---------------------------------------------------------------- requêtes

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

    private static string Luid(LUID l) => $"{l.HighPart:X}:{l.LowPart:X}";

    private static LUID ParseLuid(string s)
    {
        var parts = s.Split(':');
        return new LUID
        {
            HighPart = int.Parse(parts[0], System.Globalization.NumberStyles.HexNumber),
            LowPart = uint.Parse(parts[1], System.Globalization.NumberStyles.HexNumber),
        };
    }

    private static string SourceKey(in PathInfo p) => DisplayLogic.Key(Luid(p.Source.AdapterId), p.Source.Id);
    private static string TargetKey(in PathInfo p) => DisplayLogic.Key(Luid(p.Target.AdapterId), p.Target.Id);

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

    private static string AdapterPath(LUID adapter)
    {
        var a = new AdapterName
        {
            Header = new InfoHeader { Type = InfoGetAdapterName, Size = Marshal.SizeOf<AdapterName>(), AdapterId = adapter },
            AdapterDevicePath = "",
        };
        return DisplayConfigGetDeviceInfo(ref a) == 0 ? a.AdapterDevicePath ?? "" : "";
    }

    private static bool IsVirtualTarget(in PathInfo p) => MonitorPath(p).Contains("MTT1337", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rectangle de la source d'un chemin actif (mode source), vide si inconnu.</summary>
    private static DisplayRect SourceRect(in PathInfo p, ModeInfo[] modes)
    {
        var i = p.Source.ModeInfoIdx;
        if (i == ModeIdxInvalid || i >= modes.Length || modes[i].InfoType != ModeTypeSource) return default;
        var m = modes[i];
        return new DisplayRect((int)(uint)(m.U1 >> 32), (int)(uint)m.U2, (int)(uint)m.U0, (int)(uint)(m.U0 >> 32));
    }

    private static List<PathDesc> Describe(PathInfo[] paths, ModeInfo[] modes)
    {
        var list = new List<PathDesc>(paths.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            var p = paths[i];
            var active = (p.Flags & PathActive) != 0;
            var r = active ? SourceRect(p, modes) : default;
            list.Add(new PathDesc(i, SourceKey(p), TargetKey(p), active, IsVirtualTarget(p), SourceName(p),
                Primary: active && r.X == 0 && r.Y == 0 && r.Width > 0));
        }
        return list;
    }

    private static IEnumerable<(string Key, LUID Luid)> Adapters(PathInfo[] paths, ModeInfo[] modes) =>
        paths.SelectMany(p => new[] { p.Source.AdapterId, p.Target.AdapterId }).Concat(modes.Select(m => m.AdapterId))
            .Select(l => (Key: Luid(l), Luid: l)).DistinctBy(x => x.Key);

    // ---------------------------------------------------------------- sauvegarde / restauration

    private sealed record Saved(string Paths, string Modes, Dictionary<string, string> Adapters);

    private static byte[] ToBytes<T>(T[] items) where T : struct => MemoryMarshal.AsBytes(items.AsSpan()).ToArray();
    private static T[] FromBytes<T>(byte[] bytes) where T : struct => MemoryMarshal.Cast<byte, T>(bytes).ToArray();

    /// <summary>« moniteur@source(x,y,LxH) » par écran, trié : deux dispositions identiques ont le même résumé.</summary>
    private static string Summarize(PathInfo[] paths, ModeInfo[] modes)
    {
        var groups = new Dictionary<string, int>();
        var items = paths.Select(p =>
        {
            var mon = MonitorPath(p);
            var parts = mon.Split('#');
            return (Mon: parts.Length > 2 ? $"{parts[1]}#{parts[2]}" : mon, Src: SourceKey(p), Rect: SourceRect(p, modes));
        }).OrderBy(x => x.Mon, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        foreach (var (mon, src, r) in items)
        {
            if (!groups.TryGetValue(src, out var g)) groups[src] = g = groups.Count + 1;
            if (sb.Length > 0) sb.Append(" ; ");
            sb.Append($"{mon}@s{g}({r.X},{r.Y},{r.Width}x{r.Height})");
        }
        return sb.ToString();
    }

    public DisplaySnapshot Save()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        var adapters = Adapters(paths, modes).ToDictionary(x => x.Key, x => AdapterPath(x.Luid));
        var data = JsonSerializer.Serialize(new Saved(Convert.ToBase64String(ToBytes(paths)), Convert.ToBase64String(ToBytes(modes)), adapters));
        return new DisplaySnapshot(data, Summarize(paths, modes));
    }

    /// <summary>Chemins et modes sauvegardés, LUID d'adaptateurs ramenés à ceux d'aujourd'hui (redémarrage entre-temps).</summary>
    private static (PathInfo[] Paths, ModeInfo[] Modes) Decode(DisplaySnapshot s)
    {
        var saved = JsonSerializer.Deserialize<Saved>(s.Data) ?? throw new InvalidOperationException("sauvegarde illisible");
        var paths = FromBytes<PathInfo>(Convert.FromBase64String(saved.Paths));
        var modes = FromBytes<ModeInfo>(Convert.FromBase64String(saved.Modes));
        var (all, allModes) = Query(QdcAllPaths);
        var current = Adapters(all, allModes).Select(x => (x.Key, AdapterPath(x.Luid))).ToList();
        var map = DisplayLogic.RemapAdapters(saved.Adapters.Select(kv => (kv.Key, kv.Value)), current);
        if (map.Count > 0)
        {
            LUID M(LUID l) => map.TryGetValue(Luid(l), out var n) ? ParseLuid(n) : l;
            for (var i = 0; i < paths.Length; i++)
            {
                paths[i].Source.AdapterId = M(paths[i].Source.AdapterId);
                paths[i].Target.AdapterId = M(paths[i].Target.AdapterId);
            }
            for (var i = 0; i < modes.Length; i++) modes[i].AdapterId = M(modes[i].AdapterId);
        }
        return (paths, modes);
    }

    public void Restore(DisplaySnapshot snapshot)
    {
        var (paths, modes) = Decode(snapshot);
        // Exactement la disposition sauvegardée ; si Windows la refuse telle quelle, en le laissant ajuster les détails.
        var r = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, SdcApply | SdcUseSuppliedDisplayConfig | SdcSaveToDatabase);
        if (r == 0) return;
        r = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, SdcApply | SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcAllowChanges);
        if (r != 0) throw new InvalidOperationException($"SetDisplayConfig (restauration) : erreur {r}");
    }

    public void ResetToDatabase()
    {
        var r = SetDisplayConfig(0, null, 0, null, SdcApply | SdcUseDatabaseCurrent);
        if (r != 0) throw new InvalidOperationException($"SetDisplayConfig (disposition mémorisée) : erreur {r}");
    }

    // ---------------------------------------------------------------- écran virtuel

    public bool VirtualConnected()
    {
        var (paths, _) = Query(QdcAllPaths);
        return paths.Any(p => p.Target.TargetAvailable != 0 && IsVirtualTarget(p));
    }

    public void PlaceVirtual(DisplaySnapshot? basis, TvLayout layout, string? cloneOf, int width, int height)
    {
        // Écrans à garder tels quels : la disposition sauvegardée, ou l'actuelle sans l'écran virtuel.
        var (basePaths, baseModes) = basis != null ? Decode(basis) : Query(QdcOnlyActivePaths);
        var keep = basePaths.Where(p => !IsVirtualTarget(p)).ToArray();
        if (keep.Length == 0) throw new InvalidOperationException("aucun autre écran allumé");
        var keptDesc = Describe(keep, baseModes);

        var (all, allModes) = Query(QdcAllPaths);
        var allDesc = Describe(all, allModes);
        var cloneSource = layout == TvLayout.Duplicate ? DisplayLogic.CloneSource(keptDesc, cloneOf) : null;
        var index = DisplayLogic.ChooseVirtualPath(allDesc, keptDesc.Select(d => d.SourceKey), cloneSource)
                    ?? throw new InvalidOperationException(layout == TvLayout.Duplicate
                        ? "Windows ne propose pas l'écran virtuel en recopie de cet écran"
                        : "aucune sortie libre pour l'écran virtuel");
        var path = all[index];
        path.Flags |= PathActive;
        path.Target.ModeInfoIdx = ModeIdxInvalid;
        path.Source.ModeInfoIdx = ModeIdxInvalid;
        if (cloneSource != null)
        {
            // Recopie : même mode source que les écrans de cette source (le clone existant n'est pas modifié).
            var twin = keep.First(p => SourceKey(p) == cloneSource);
            path.Source.ModeInfoIdx = twin.Source.ModeInfoIdx;
        }
        var apply = keep.Append(path).ToArray();
        var r = SetDisplayConfig((uint)apply.Length, apply, (uint)baseModes.Length, baseModes,
            SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
        if (r != 0) throw new InvalidOperationException($"SetDisplayConfig ({layout}) : erreur {r}");

        if (layout == TvLayout.Extend)
        {
            // Écran à part : à la définition envoyée à la box (sinon l'image serait mise à l'échelle).
            var name = SourceName(path);
            var mode = DisplayLogic.PickMode(DisplayNative.Modes(name), width, height);
            if (mode != null)
            {
                var res = DisplayNative.SetResolution(name, mode.Value);
                Diagnostics.Log.Info($"Écran virtuel : {name} en {mode.Value.Width}x{mode.Value.Height}@{mode.Value.Hz} : {DisplayNative.Describe(res)}");
            }
        }
    }

    public VirtualInfo? Virtual()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        var desc = Describe(paths, modes);
        var v = desc.FirstOrDefault(d => d.Virtual);
        if (v == null) return null;
        return new VirtualInfo(v.SourceName ?? "", SourceRect(paths[v.Index], modes), DisplayLogic.LayoutOf(desc));
    }

    /// <summary>Chemins actifs, pour le journal. Lecture seule.</summary>
    public static string DescribeActive()
    {
        try
        {
            var (paths, modes) = Query(QdcOnlyActivePaths);
            return Summarize(paths, modes);
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Tous les chemins possibles (diagnostic : KairoCast.exe --displays). Lecture seule.</summary>
    public static string DescribeAll()
    {
        try
        {
            var (paths, _) = Query(QdcAllPaths);
            return string.Join("\n", paths.Select(p =>
                $"  {((p.Flags & PathActive) != 0 ? "actif  " : "inactif")} source {SourceKey(p)} {SourceName(p)} → cible {TargetKey(p)} {MonitorPath(p)} dispo={p.Target.TargetAvailable}"));
        }
        catch (Exception e) { return e.Message; }
    }

    // ---------------------------------------------------------------- fenêtres

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;
    private const uint SpiGetWorkArea = 0x30;
    private const int SwShowMaximized = 3;
    private const int SwShowMinimized = 2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, out RECT rect, uint winIni);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    public IReadOnlyList<WindowDesc> Windows()
    {
        var list = new List<WindowDesc>();
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassNameW(h, cls, cls.Capacity);
            var title = new StringBuilder(256);
            GetWindowTextW(h, title, title.Capacity);
            var ex = GetWindowLongPtrW(h, GwlExStyle).ToInt64();
            var cloaked = DwmGetWindowAttribute(h, DwmwaCloaked, out var c, sizeof(int)) == 0 && c != 0;
            GetWindowRect(h, out var r);
            list.Add(new WindowDesc(h.ToInt64(), cls.ToString(), IsWindowVisible(h), (ex & WsExToolWindow) != 0, cloaked,
                new DisplayRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public DisplayRect PrimaryWorkArea()
    {
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out var r, 0)) throw new InvalidOperationException("zone de travail inconnue");
        return new DisplayRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public void MoveWindow(long handle, DisplayRect to)
    {
        var h = new IntPtr(handle);
        if (!IsWindow(h)) return;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(h, ref wp) && wp.showCmd is SwShowMaximized or SwShowMinimized)
        {
            // Agrandie ou réduite : on déplace sa position « normale », elle s'agrandit alors sur l'écran principal.
            wp.rcNormalPosition = new RECT { Left = to.X, Top = to.Y, Right = to.X + to.Width, Bottom = to.Y + to.Height };
            SetWindowPlacement(h, ref wp);
            return;
        }
        SetWindowPos(h, IntPtr.Zero, to.X, to.Y, to.Width, to.Height, SwpNoZOrder | SwpNoActivate);
    }
}
