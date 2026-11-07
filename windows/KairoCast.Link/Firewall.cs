using System.ComponentModel;
using System.Diagnostics;

namespace KairoCast.Link;

/// <summary>
/// Pare-feu Windows : crée au besoin les règles entrantes (UDP 5353 pour mDNS, TCP pour l'exécutable)
/// avec une seule demande UAC. À appeler au démarrage de l'appli ; n'échoue jamais (renvoie false et journalise).
/// </summary>
public static class Firewall
{
    private const string RuleUdp = "Kairo Cast mDNS";
    private const string RuleTcp = "Kairo Cast TCP";

    public static bool EnsureRule(Action<string>? log = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { log?.Invoke("Pare-feu : chemin de l'exécutable inconnu"); return false; }
            if (RuleExists(RuleUdp) && RuleExists(RuleTcp)) return true;

            var cmd =
                $"netsh advfirewall firewall delete rule name=\"{RuleUdp}\" & " +
                $"netsh advfirewall firewall delete rule name=\"{RuleTcp}\" & " +
                $"netsh advfirewall firewall add rule name=\"{RuleUdp}\" dir=in action=allow protocol=UDP localport=5353 profile=private,domain & " +
                $"netsh advfirewall firewall add rule name=\"{RuleTcp}\" dir=in action=allow protocol=TCP program=\"{exe}\" profile=private,domain";
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                Verb = "runas", UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) { log?.Invoke("Pare-feu : processus non démarré"); return false; }
            p.WaitForExit(30000);
            var ok = RuleExists(RuleUdp) && RuleExists(RuleTcp);
            if (!ok) log?.Invoke("Pare-feu : règles non créées");
            return ok;
        }
        catch (Win32Exception ex) { log?.Invoke("Pare-feu : élévation refusée ou impossible (" + ex.Message + ")"); return false; }
        catch (Exception ex) { log?.Invoke("Pare-feu : " + ex.Message); return false; }
    }

    private static bool RuleExists(string name)
    {
        var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return p.ExitCode == 0;
    }
}
