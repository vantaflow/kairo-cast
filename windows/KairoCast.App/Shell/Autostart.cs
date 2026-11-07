using Microsoft.Win32;

namespace KairoCast.App.Shell;

/// <summary>Démarrage avec Windows : valeur « KairoCast » de HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KairoCast";

    public static string Command(string exePath) => $"\"{exePath}\" --tray";

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return false;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, Command(exe));
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log.Warn("Démarrage avec Windows : " + e.Message);
            return false;
        }
    }
}
