using System.Text.Json;
using System.Text.Json.Serialization;
using KairoCast.Media;

namespace KairoCast.App.Shell;

/// <summary>Réglages de l'application, enregistrés dans %APPDATA%\KairoCast\settings.json. Un fichier absent ou corrompu donne les valeurs par défaut.</summary>
public sealed class AppSettings
{
    public bool ReplaceWinK { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public QualityPreset Quality { get; set; } = QualityPreset.P1080F30;
    public bool SendAudio { get; set; } = true;
    /// <summary>La demande de règle de pare-feu a déjà été faite (acceptée ou refusée) : on ne la repose pas.</summary>
    public bool FirewallAsked { get; set; }
    /// <summary>Adresses ajoutées à la main, « ip:port ».</summary>
    public List<string> ManualBoxes { get; set; } = new();

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
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public bool Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json;
            lock (this) json = JsonSerializer.Serialize(this, Json);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
