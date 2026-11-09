using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using KairoCast.App.Diagnostics;
using Vortice.DXGI;

namespace KairoCast.App.Display;

/// <summary>
/// Écran virtuel fourni par le pilote open source Virtual Display Driver (VirtualDrivers, MIT, signé par SignPath
/// Foundation ; matériel Root\MttVDD, moniteur EDID « MTT1337 »).
///
/// Au repos, le périphérique est <b>désactivé</b> : Windows ne voit aucun écran de plus (Win+P, Paramètres, branchement
/// d'un écran ne peuvent pas l'allumer). Il n'est activé que pendant un partage, puis désactivé.
/// Le pilote ne sait pas débrancher son moniteur sans être désactivé, et l'activer demande des droits d'administrateur :
/// une préparation unique (une demande UAC, sur clic) crée deux tâches planifiées « pnputil /enable-device » et
/// « /disable-device » limitées à ce périphérique, que l'appli lance ensuite sans élévation.
///
/// Installation (sur clic) : <c>winget install VirtualDrivers.Virtual-Display-Driver</c> (paquet portable, sans
/// élévation), puis KairoCast.exe <c>--vdd-setup</c> élevé : certificat de l'éditeur dans « Éditeurs approuvés »,
/// vdd_settings.xml, <c>devcon install … Root\MttVDD</c>, désactivation immédiate, tâches planifiées.
/// La disposition des écrans est sauvegardée avant et restaurée après (l'arrivée du moniteur peut faire basculer Windows).
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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);

    /// <summary>Le périphérique du pilote : présent ou non, code de problème (0 = actif, 22 = désactivé), identifiant d'instance.</summary>
    public readonly record struct DeviceState(bool Installed, int Problem, string InstanceId);

    /// <summary>Lecture seule (SetupAPI) : aucun effet sur le périphérique.</summary>
    public static DeviceState Detect()
    {
        var cls = DisplayClass;
        var set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, DigcfPresent);
        if (set == new IntPtr(-1)) return default;
        try
        {
            for (var i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                var buf = new byte[1024];
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out _, buf, buf.Length, out var req)) continue;
                var ids = Encoding.Unicode.GetString(buf, 0, Math.Min(req, buf.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!ids.Any(DisplayLogic.IsVddHardwareId)) continue;
                var sb = new StringBuilder(256);
                var instance = SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : "";
                var cr = CM_Get_DevNode_Status(out _, out var problem, data.DevInst, 0);
                return new DeviceState(true, cr == 0 ? problem : -1, instance);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return default;
    }

    // ---------------------------------------------------------------- instance partagée

    private static readonly Lazy<VirtualScreen> LazyScreen = new(() =>
        new VirtualScreen(new DisplaySystem(), new VirtualDevice(), new FileRestoreStore(FileRestoreStore.DefaultPath), Log.Info));

    /// <summary>L'écran virtuel de l'application (une seule instance : la même sauvegarde pour tous les chemins d'arrêt).</summary>
    public static VirtualScreen Screen => LazyScreen.Value;

    /// <summary>Dernier recours (exception non gérée, fermeture de session Windows) : restaurer si un partage était en cours.</summary>
    public static void EmergencyStop()
    {
        if (LazyScreen.IsValueCreated && LazyScreen.Value.Active) LazyScreen.Value.Stop();
    }

    // ---------------------------------------------------------------- installation et préparation (sur clic seulement)

    /// <summary>
    /// Installe le pilote si besoin (winget, sans élévation) et le prépare pour Kairo Cast (une demande UAC).
    /// Disposition des écrans sauvegardée avant et restaurée après. Renvoie null si tout va bien, sinon un message.
    /// À appeler hors du fil de l'interface, uniquement après un clic explicite de l'utilisateur.
    /// </summary>
    public static string? Setup(Action<string> status)
    {
        Log.Info("Écran virtuel : installation/préparation demandée par l'utilisateur");
        var state = Detect();
        string? pkg = null;
        if (!state.Installed)
        {
            pkg = FindPackageDir();
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
        }

        status(state.Installed
            ? "Activation de l'écran virtuel pour Kairo Cast : acceptez la demande de Windows…"
            : "Installation du pilote d'écran virtuel : acceptez la demande de Windows…");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("chemin de KairoCast.exe inconnu");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("utilisateur Windows inconnu");
        var err = Screen.Guard(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    ArgumentList = { "--vdd-setup", sid, pkg ?? "" },
                    WindowStyle = ProcessWindowStyle.Hidden,
                })!;
                if (!p.WaitForExit(TimeSpan.FromMinutes(3))) return "La préparation du pilote ne répond pas (voir le journal).";
                Log.Info($"Écran virtuel : préparation élevée terminée, code {p.ExitCode}");
                return p.ExitCode == 0 ? null : $"La préparation du pilote a échoué (code {p.ExitCode}, voir le journal).";
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                Log.Warn("Écran virtuel : élévation refusée par l'utilisateur");
                return "Annulé : Windows n'a pas reçu l'autorisation.";
            }
        });
        VirtualDevice.ForgetTasks();
        return err;
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

    private static string Pnputil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    /// <summary>
    /// Partie élevée (KairoCast.exe --vdd-setup &lt;SID&gt; &lt;paquet ou vide&gt;) : installe le pilote s'il manque, le
    /// désactive (état de repos) et crée les deux tâches planifiées. Code 0 si réussi.
    /// </summary>
    public static int SetupElevated(string userSid, string pkg)
    {
        try
        {
            Log.Info($"Écran virtuel (élevé) : préparation pour {userSid}, paquet « {pkg} »");
            if (!Detect().Installed)
            {
                var code = InstallDriver(pkg);
                if (code != 0) return code;
            }
            var sw = Stopwatch.StartNew();
            DeviceState st;
            while (!(st = Detect()).Installed || st.InstanceId.Length == 0)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(20)) { Log.Error("Écran virtuel (élevé) : périphérique introuvable après installation"); return 6; }
                Thread.Sleep(250);
            }
            if (st.Problem != DisplayLogic.ProblemDisabled)
            {
                var (dc, dout) = Run(Pnputil, DisplayLogic.PnputilArgs(false, st.InstanceId), TimeSpan.FromMinutes(1));
                Log.Info($"Écran virtuel (élevé) : désactivation (état de repos) : {dc} {Tail(dout)}");
            }
            foreach (var enable in new[] { true, false })
            {
                var xml = Path.Combine(Path.GetTempPath(), $"kairocast-task-{(enable ? "on" : "off")}-{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(xml, DisplayLogic.TaskXml(enable, st.InstanceId, userSid, Pnputil), Encoding.Unicode);
                    var (tc, tout) = Run("schtasks", $"/create /tn \"{DisplayLogic.TaskName(enable)}\" /xml \"{xml}\" /f", TimeSpan.FromMinutes(1));
                    Log.Info($"Écran virtuel (élevé) : tâche {DisplayLogic.TaskName(enable)} : {tc} {Tail(tout)}");
                    if (tc != 0) return 7;
                }
                finally { try { File.Delete(xml); } catch { } }
            }
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("Écran virtuel (élevé) : préparation impossible", e);
            return 5;
        }
    }

    private static int InstallDriver(string pkg)
    {
        if (string.IsNullOrEmpty(pkg)) { Log.Error("Écran virtuel (élevé) : paquet du pilote absent"); return 2; }
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

    internal static (int Code, string Output) Run(string file, string args, TimeSpan timeout, string? cwd = null)
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
            var sb = new StringBuilder();
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

    internal static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Any(char.IsLetterOrDigit)).TakeLast(4);
        return string.Join(" | ", lines);
    }
}

/// <summary>
/// Implémentation Windows de <see cref="IVirtualDevice"/> : état par SetupAPI, activation et désactivation par les
/// tâches planifiées créées à la préparation (schtasks /run, sans élévation).
/// </summary>
internal sealed class VirtualDevice : IVirtualDevice
{
    private static bool? _tasks;
    private static DateTime _tasksCheckedAt;

    /// <summary>Les tâches viennent d'être créées (ou supprimées) : revérifier.</summary>
    public static void ForgetTasks() => _tasks = null;

    private static bool TasksExist()
    {
        if (_tasks == true || (_tasks == false && DateTime.UtcNow - _tasksCheckedAt < TimeSpan.FromSeconds(30))) return _tasks.Value;
        var ok = new[] { true, false }.All(on =>
            VirtualDisplay.Run("schtasks", $"/query /tn \"{DisplayLogic.TaskName(on)}\"", TimeSpan.FromSeconds(15)).Code == 0);
        _tasks = ok;
        _tasksCheckedAt = DateTime.UtcNow;
        return ok;
    }

    public VddStatus Status()
    {
        var st = VirtualDisplay.Detect();
        if (!st.Installed) return VddStatus.NotInstalled;
        return TasksExist() ? VddStatus.Ready : VddStatus.NeedsSetup;
    }

    public bool IsEnabled()
    {
        var st = VirtualDisplay.Detect();
        return st.Installed && st.Problem == 0;
    }

    public void SetEnabled(bool on)
    {
        var (code, output) = VirtualDisplay.Run("schtasks", $"/run /tn \"{DisplayLogic.TaskName(on)}\"", TimeSpan.FromSeconds(15));
        Log.Info($"Écran virtuel : {(on ? "activation" : "désactivation")} demandée ({code} {VirtualDisplay.Tail(output)})");
        if (code != 0) throw new InvalidOperationException($"tâche {DisplayLogic.TaskName(on)} non lancée (code {code})");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            var st = VirtualDisplay.Detect();
            if (on ? st.Installed && st.Problem == 0 : !st.Installed || st.Problem == DisplayLogic.ProblemDisabled) return;
            Thread.Sleep(200);
        }
        throw new InvalidOperationException(on ? "le pilote ne s'est pas activé" : "le pilote ne s'est pas désactivé");
    }
}
