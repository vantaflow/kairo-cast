using System.Runtime.InteropServices;

namespace KairoCast.App.Display;

/// <summary>
/// API d'affichage GDI de user32 : énumération des écrans, modes, allumage/extinction d'un écran sur le bureau.
/// Aucune de ces fonctions ne demande de droits d'administrateur.
/// </summary>
internal static class DisplayNative
{
    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;
    private const int MirroringDriver = 0x8;
    private const int DmPosition = 0x20;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint CdsNoReset = 0x10000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hmon, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, int index, ref DISPLAY_DEVICE dd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hmon, ref MONITORINFOEX info);

    private static DEVMODE NewDevMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    /// <summary>Tous les écrans connus de Windows (allumés ou non), hors pilotes miroir.</summary>
    public static List<DisplayInfo> List()
    {
        var list = new List<DisplayInfo>();
        for (var i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & MirroringDriver) != 0) continue;
            // Un écran en recopie porte plusieurs moniteurs : l'écran virtuel recopié ne doit pas masquer l'écran réel.
            var monitors = new List<string>();
            for (var m = 0; m < 8; m++)
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevicesW(dd.DeviceName, m, ref mon, 0)) break;
                if (!string.IsNullOrEmpty(mon.DeviceID)) monitors.Add(mon.DeviceID);
            }
            var monitorId = monitors.FirstOrDefault(id => !id.Contains("MTT1337", StringComparison.OrdinalIgnoreCase)) ?? monitors.FirstOrDefault() ?? "";
            var attached = (dd.StateFlags & AttachedToDesktop) != 0;
            // Sortie sans écran branché : ignorée, sauf celle du pilote virtuel (son moniteur n'apparaît qu'une fois allumé).
            if (monitorId.Length == 0 && !attached
                && !DisplayLogic.IsVirtual(new DisplayInfo(dd.DeviceName, dd.DeviceString ?? "", dd.DeviceID ?? "", "", false, false)))
                continue;
            int x = 0, y = 0, w = 0, h = 0;
            var dm = NewDevMode();
            if (attached && EnumDisplaySettingsW(dd.DeviceName, EnumCurrentSettings, ref dm))
            {
                x = dm.dmPositionX; y = dm.dmPositionY; w = dm.dmPelsWidth; h = dm.dmPelsHeight;
            }
            list.Add(new DisplayInfo(dd.DeviceName, dd.DeviceString ?? "", dd.DeviceID ?? "", monitorId, attached,
                (dd.StateFlags & PrimaryDevice) != 0, x, y, w, h));
        }
        return list;
    }

    /// <summary>Modes annoncés par un écran (allumé ou non).</summary>
    public static List<DisplayMode> Modes(string device)
    {
        var modes = new List<DisplayMode>();
        for (var i = 0; i < 2000; i++)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsW(device, i, ref dm)) break;
            modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        }
        return modes;
    }

    /// <summary>Allume l'écran sur le bureau (étendu) à cette position et dans ce mode. Renvoie le code DISP_CHANGE (0 = réussi).</summary>
    public static int Attach(string device, int x, int y, DisplayMode mode)
    {
        var dm = NewDevMode();
        dm.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | (mode.Hz > 0 ? DmDisplayFrequency : 0);
        dm.dmPositionX = x;
        dm.dmPositionY = y;
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        var r = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
        if (r != 0) return r;
        return ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>Retire l'écran du bureau (définition 0×0) ; les fenêtres reviennent sur les autres écrans.</summary>
    public static int Detach(string device)
    {
        var dm = NewDevMode();
        dm.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
        var r = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
        if (r != 0) return r;
        return ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>HMONITOR de « \\.\DISPLAYn », ou 0 s'il n'est pas (encore) sur le bureau.</summary>
    public static IntPtr MonitorFor(string device)
    {
        var found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfoW(h, ref mi) && string.Equals(mi.szDevice, device, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Describe(int dispChange) => dispChange switch
    {
        0 => "réussi",
        1 => "redémarrage nécessaire",
        -1 => "échec du pilote",
        -2 => "mode non pris en charge",
        -3 => "registre non modifiable",
        -4 => "options invalides",
        -5 => "paramètre invalide",
        -6 => "écran occupé",
        -7 => "refusé (session distante ou verrouillée)",
        _ => $"code {dispChange}",
    };
}
