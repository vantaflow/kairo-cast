using System.Text.Json;
using System.Text.Json.Serialization;
using KairoCast.Media;

namespace KairoCast.App.Shell;

/// <summary>Comme le panneau Win+K de Windows : recopier un écran du PC, ou ajouter un écran (virtuel) affiché par la TV.</summary>
public enum ShareMode { Duplicate, Extend }

/// <summary>Réglages de l'application, enregistrés dans %APPDATA%\KairoCast\settings.json. Un fichier absent ou corrompu donne les valeurs par défaut.</summary>
public sealed class AppSettings
{
    public bool ReplaceWinK { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public QualityPreset Quality { get; set; } = QualityPreset.P1080F30;
    public bool SendAudio { get; set; } = true;
    /// <summary>Dupliquer (par défaut, comportement historique) ou Étendre (écran virtuel).</summary>
    public ShareMode ShareMode { get; set; } = ShareMode.Duplicate;
    /// <summary>Écran recopié en mode Dupliquer (« \\.\DISPLAY2 ») ; null ou absent = écran principal.</summary>
    public string? DuplicateScreen { get; set; }
    /// <summary>L'écran virtuel a été allumé par l'appli et pas encore éteint (arrêt brutal) : il est éteint au démarrage suivant.</summary>
    public bool VirtualDisplayOn { get; set; }
    /// <summary>La demande de règle de pare-feu a déjà été faite (acceptée ou refusée) : on ne la repose pas.</summary>
    public bool FirewallAsked { get; set; }
    /// <summary>Adresses ajoutées à la main, « ip:port ».</summary>
    public List<string> ManualBoxes { get; set; } = new();

    private static readonly object SaveLock = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KairoCast", "settings.json");

    /// <summary>Options de session correspondant à la qualité et au son choisis.</summary>
    public StreamOptions ToStreamOptions() => QualityPresets.Get(Quality, SendAudio);

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var text = File.ReadAllText(path ?? DefaultPath);
            var s = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
            s.ManualBoxes ??= new List<string>();
            if (!Enum.IsDefined(s.Quality)) s.Quality = QualityPreset.P1080F30;
            if (!Enum.IsDefined(s.ShareMode)) s.ShareMode = ShareMode.Duplicate;
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>Modifie la liste des adresses manuelles sous verrou (appelable depuis n'importe quel fil).</summary>
    public bool AddManualBox(string entry)
    {
        lock (SaveLock)
        {
            if (ManualBoxes.Contains(entry)) return false;
            ManualBoxes.Add(entry);
            return true;
        }
    }

    public bool Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            lock (SaveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(this, Json);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
