using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using KairoCast.App.Diagnostics;
using Vortice.DXGI;

namespace KairoCast.App.Display;

public enum VddState { NotInstalled, Ready, Problem }

/// <summary>
/// Écran virtuel du mode Étendre, fourni par le pilote open source Virtual Display Driver (VirtualDrivers, MIT,
/// signé par SignPath Foundation).
///
/// Installation (une fois) : <c>winget install VirtualDrivers.Virtual-Display-Driver</c> (paquet portable, sans élévation :
/// application VDD Control, devcon et pilote signé), puis une seule élévation UAC où KairoCast.exe se relance avec
/// <c>--vdd-install</c> : certificat de l'éditeur dans « Éditeurs approuvés », vdd_settings.xml, <c>devcon install … Root\MttVDD</c>.
///
/// Ensuite, aucune élévation : l'écran virtuel reste installé (un moniteur, cf. vdd_settings.xml) mais est retiré du
/// bureau hors partage. Allumer/éteindre passe par SetDisplayConfig (<see cref="DisplayConfig"/>), la définition et la
/// position par ChangeDisplaySettingsEx, comme le panneau Paramètres › Écran.

/// Le tube de contrôle du pilote (\\.\pipe\MTTVirtualDisplayPipe, ouvert à tous) n'est pas utilisé : SETDISPLAYCOUNT
/// réécrit vdd_settings.xml (dossier protégé) et recharge l'adaptateur, plus lent et moins fiable.
/// </summary>
internal static class VirtualDisplay
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private const int DigcfPresent = 0x2;
    private const int SpdrpHardwareId = 0x1;
    private const int ErrorCancelled = 1223;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, string? enumerator, IntPtr hwnd, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int regType, byte[]? buffer, int size, out int required);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);

    /// <summary>État du pilote : absent, prêt, ou présent avec un problème (désactivé…), avec le code du Gestionnaire de périphériques.</summary>
    public static (VddState State, int Problem) Detect()
    {
        var cls = DisplayClass;
        var set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, DigcfPresent);
        if (set == new IntPtr(-1)) return (VddState.NotInstalled, 0);
        try
        {
            for (var i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                var buf = new byte[1024];
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out _, buf, buf.Length, out var req)) continue;
                var ids = System.Text.Encoding.Unicode.GetString(buf, 0, Math.Min(req, buf.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!ids.Any(DisplayLogic.IsVddHardwareId)) continue;
                var cr = CM_Get_DevNode_Status(out _, out var problem, data.DevInst, 0);
                return cr == 0 && problem == 0 ? (VddState.Ready, 0) : (VddState.Problem, problem);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return (VddState.NotInstalled, 0);
    }

    // ---------------------------------------------------------------- installation

    /// <summary>
    /// Installe le pilote : winget (sans élévation) puis KairoCast.exe --vdd-install élevé (une demande UAC).
    /// Renvoie null si tout va bien, sinon un message prêt à afficher. À appeler hors du fil de l'interface.
    /// </summary>
    public static string? Install(Action<string> status)
    {
        Log.Info("Écran virtuel : installation demandée");
        var pkg = FindPackageDir();
        if (pkg == null)
        {
            status("Téléchargement du pilote d'écran virtuel (winget)…");
            var (code, output) = Run("winget", DisplayLogic.WingetInstallArgs, TimeSpan.FromMinutes(10));
            Log.Info($"Écran virtuel : winget a rendu {code} : {Tail(output)}");
            pkg = FindPackageDir();
            if (pkg == null)
                return code == int.MinValue
                    ? "winget est introuvable : installez « App Installer » depuis le Microsoft Store, puis réessayez."
                    : "Le téléchargement du pilote par winget a échoué (voir le journal).";
        }
        Log.Info($"Écran virtuel : paquet winget dans {pkg}");

        status("Installation du pilote d'écran virtuel : acceptez la demande de Windows…");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("chemin de KairoCast.exe inconnu");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "--vdd-install", pkg },
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
                return "L'installation du pilote ne répond pas (voir le journal).";
            Log.Info($"Écran virtuel : installation élevée terminée, code {p.ExitCode}");
            if (p.ExitCode != 0) return $"L'installation du pilote a échoué (code {p.ExitCode}, voir le journal).";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn("Écran virtuel : élévation refusée par l'utilisateur");
            return "Installation annulée : Windows n'a pas reçu l'autorisation.";
        }

        // Windows allume d'office le nouvel écran : on l'éteint, il ne servira que pendant les partages.
        status("Préparation de l'écran virtuel…");
        var sw = Stopwatch.StartNew();
        DisplayInfo? v = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (v = DisplayLogic.FindVirtual(DisplayNative.List())) == null) Thread.Sleep(250);
        if (v == null) return "Le pilote est installé mais l'écran virtuel n'apparaît pas (redémarrer le PC peut être nécessaire).";
        Thread.Sleep(1000);
        Deactivate();
        return null;
    }

    private static string? FindPackageDir()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"),
        };
        var dirs = roots.Where(Directory.Exists).SelectMany(r =>
        {
            try { return Directory.GetDirectories(r); } catch { return Array.Empty<string>(); }
        });
        var dir = DisplayLogic.PickPackageDir(dirs);
        return dir != null && File.Exists(Path.Combine(dir, "Dependencies", "devcon.exe")) ? dir : null;
    }

    /// <summary>
    /// Partie élevée (KairoCast.exe --vdd-install &lt;paquet&gt;) : certificat, configuration, devcon. Code 0 si réussi.
    /// </summary>
    public static int InstallElevated(string pkg)
    {
        try
        {
            Log.Info($"Écran virtuel (élevé) : installation depuis {pkg}");
            if (Detect().State != VddState.NotInstalled)
            {
                Log.Info("Écran virtuel (élevé) : pilote déjà présent, rien à faire");
                return 0;
            }
            var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var drv = Path.Combine(pkg, DisplayLogic.DriverSubdir(arm));
            var inf = Path.Combine(drv, "MttVDD.inf");
            var cat = Path.Combine(drv, "mttvdd.cat");
            var devcon = Path.Combine(pkg, "Dependencies", "devcon.exe");
            foreach (var f in new[] { inf, cat, devcon })
                if (!File.Exists(f)) { Log.Error($"Écran virtuel (élevé) : fichier manquant {f}"); return 3; }

            Directory.CreateDirectory(DisplayLogic.ConfigDir);
            var xml = Path.Combine(DisplayLogic.ConfigDir, "vdd_settings.xml");
            if (!File.Exists(xml))
            {
                File.WriteAllText(xml, DisplayLogic.SettingsXml());
                Log.Info($"Écran virtuel (élevé) : {xml} écrit");
            }
            else Log.Info($"Écran virtuel (élevé) : {xml} existant conservé");

            // Sans cela, Windows ouvrirait une seconde fenêtre « Voulez-vous installer ce logiciel de périphérique ? ».
            var certs = new X509Certificate2Collection();
#pragma warning disable SYSLIB0057
            certs.Import(File.ReadAllBytes(cat));
#pragma warning restore SYSLIB0057
            using (var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                foreach (var c in certs)
                {
                    // Seul le certificat du signataire du pilote (pas les autorités) va dans « Éditeurs approuvés ».
                    if (!c.Subject.Contains("SignPath", StringComparison.OrdinalIgnoreCase)) continue;
                    store.Add(c);
                    Log.Info($"Écran virtuel (élevé) : certificat ajouté aux éditeurs approuvés : {c.Subject} ({c.Thumbprint})");
                }
            }

            var (code, output) = Run(devcon, $"install \"{inf}\" {DisplayLogic.HardwareId}", TimeSpan.FromMinutes(2), drv);
            Log.Info($"Écran virtuel (élevé) : devcon a rendu {code} : {Tail(output)}");
            return code is 0 or 1 ? 0 : 4; // 1 = redémarrage conseillé
        }
        catch (Exception e)
        {
            Log.Error("Écran virtuel (élevé) : installation impossible", e);
            return 5;
        }
    }

    // ---------------------------------------------------------------- allumage / extinction

    /// <summary>
    /// Allume l'écran virtuel en extension, à droite des autres, dans la définition la plus proche de celle demandée.
    /// Renvoie son nom GDI (« \\.\DISPLAY3 »). Lève une exception avec un message lisible en cas d'échec.
    /// </summary>
    public static string Activate(int width, int height)
    {
        var (state, problem) = Detect();
        if (state == VddState.NotInstalled) throw new InvalidOperationException("le pilote d'écran virtuel n'est pas installé");
        if (state == VddState.Problem)
            throw new InvalidOperationException($"le pilote d'écran virtuel est présent mais inactif (code {problem}, désactivé dans le Gestionnaire de périphériques ?)");
        if (DisplayLogic.FindVirtual(DisplayNative.List()) == null)
            throw new InvalidOperationException("l'écran virtuel n'apparaît pas dans Windows (redémarrer le PC peut être nécessaire)");
        Log.Info($"Écran virtuel : affichage avant allumage : {DisplayConfig.DescribeActive()}");
        var sw = Stopwatch.StartNew();

        // 1. Branchement en extension, sur sa propre source (le sort d'une éventuelle recopie) : API CCD.
        string name;
        try
        {
            name = DisplayConfig.Extend();
            Log.Info($"Écran virtuel : branché en extension sur {name}");
        }
        catch (Exception e)
        {
            Log.Warn($"Écran virtuel : SetDisplayConfig refusé ({e.Message}), essai par ChangeDisplaySettingsEx");
            var v = DisplayLogic.FindVirtual(DisplayNative.List())!;
            name = v.DeviceName;
            var r = DisplayNative.Attach(name, DisplayLogic.ExtendPosition(DisplayNative.List(), name).X, 0, new DisplayMode(width, height, 60));
            if (r != 0) throw new InvalidOperationException($"Windows refuse d'allumer l'écran virtuel ({DisplayNative.Describe(r)})");
        }
        while (DisplayNative.MonitorFor(name) == IntPtr.Zero && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(100);
        if (DisplayNative.MonitorFor(name) == IntPtr.Zero)
            throw new InvalidOperationException("l'écran virtuel ne s'est pas allumé");

        // 2. Définition de la box et place à droite des autres écrans : API GDI.
        var all = DisplayNative.List();
        var cur = all.FirstOrDefault(d => d.DeviceName == name);
        var mode = DisplayLogic.PickMode(DisplayNative.Modes(name), width, height) ?? new DisplayMode(width, height, 60);
        var (x, y) = DisplayLogic.ExtendPosition(all, name);
        Log.Info($"Écran virtuel : {name} « {cur?.AdapterName} » ({cur?.AdapterId}, {cur?.MonitorId}) actuellement {cur?.Width}x{cur?.Height} en {cur?.X},{cur?.Y} ; " +
                 $"demandé {width}x{height}, mode choisi {mode.Width}x{mode.Height}@{mode.Hz}, position {x},{y}");
        if (cur == null || cur.Width != mode.Width || cur.Height != mode.Height || cur.X != x || cur.Y != y)
        {
            var r = DisplayNative.Attach(name, x, y, mode);
            Log.Info($"Écran virtuel : mode et position {DisplayNative.Describe(r)}");
            if (r != 0) Log.Warn("Écran virtuel : définition non appliquée, l'image sera mise à l'échelle par l'encodeur");
        }
        while (DisplayNative.MonitorFor(name) == IntPtr.Zero && sw.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(100);
        if (DisplayNative.MonitorFor(name) == IntPtr.Zero)
            throw new InvalidOperationException("l'écran virtuel ne s'est pas allumé");
        Log.Info($"Écran virtuel : prêt en {sw.ElapsedMilliseconds} ms, sortie DXGI {DxgiOutput(name)} ; affichage : {DisplayConfig.DescribeActive()}");
        return name;
    }

    /// <summary>Éteint l'écran virtuel s'il est sur le bureau (sans élévation). Ne lève jamais d'exception.</summary>
    public static void Deactivate()
    {
        try
        {
            if (Detect().State == VddState.NotInstalled) return;
            try
            {
                var n = DisplayConfig.Remove();
                if (n > 0) Log.Info($"Écran virtuel : éteint ({n} chemin(s) retiré(s)) ; affichage : {DisplayConfig.DescribeActive()}");
                return;
            }
            catch (Exception e)
            {
                Log.Warn($"Écran virtuel : SetDisplayConfig refusé ({e.Message}), essai par ChangeDisplaySettingsEx");
            }
            foreach (var v in DisplayNative.List().Where(d => DisplayLogic.IsVirtual(d) && d.Attached && !d.Primary))
            {
                var r = DisplayNative.Detach(v.DeviceName);
                Log.Info($"Écran virtuel : extinction de {v.DeviceName} {DisplayNative.Describe(r)}");
            }

        }
        catch (Exception e)
        {
            Log.Warn($"Écran virtuel : extinction impossible : {Log.Describe(e)}");
        }
    }

    /// <summary>Adaptateur et index de la sortie DXGI qui porte cet écran (pour le journal).</summary>
    public static string DxgiOutput(string deviceName)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                                return $"« {adapter.Description1.Description} » n°{a}, sortie n°{o}";
                        }
                    }
                }
            }
            return "introuvable";
        }
        catch (Exception e)
        {
            return $"inconnue ({Log.Describe(e)})";
        }
    }

    // ---------------------------------------------------------------- outils

    private static (int Code, string Output) Run(string file, string args, TimeSpan timeout, string? cwd = null)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = cwd ?? Environment.CurrentDirectory,
                },
            };
            var sb = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch { }
                return (-1, "délai dépassé");
            }
            p.WaitForExit();
            lock (sb) return (p.ExitCode, sb.ToString());
        }
        catch (Win32Exception e)
        {
            Log.Warn($"Écran virtuel : lancement de {file} impossible : {e.Message}");
            return (int.MinValue, e.Message);
        }
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Any(char.IsLetterOrDigit)).TakeLast(4);
        return string.Join(" | ", lines);
    }
}
