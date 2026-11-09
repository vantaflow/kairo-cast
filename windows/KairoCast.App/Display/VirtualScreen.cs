using System.Text.Json;

namespace KairoCast.App.Display;

/// <summary>État du pilote d'écran virtuel pour Kairo Cast.</summary>
public enum VddStatus
{
    /// <summary>Pilote absent : installation sur clic explicite (winget, puis une demande UAC).</summary>
    NotInstalled,
    /// <summary>Pilote présent mais pas encore préparé pour Kairo Cast (tâches d'activation absentes) : une demande UAC.</summary>
    NeedsSetup,
    /// <summary>Prêt : désactivé au repos, activé seulement pendant un partage, sans élévation.</summary>
    Ready,
}

/// <summary>Disposition complète des écrans (chemins et modes de QueryDisplayConfig), opaque pour la logique.</summary>
/// <param name="Data">Données brutes sérialisées par l'implémentation Windows.</param>
/// <param name="Summary">Description canonique (pour comparer deux dispositions et pour le journal).</param>
public sealed record DisplaySnapshot(string Data, string Summary);

/// <summary>L'écran virtuel tel que Windows l'affiche : nom GDI de sa source (« \\.\DISPLAY1 » s'il recopie l'écran 1).</summary>
public sealed record VirtualInfo(string DeviceName, DisplayRect Bounds, TvLayout Layout);

/// <summary>Affichage de Windows (API CCD, fenêtres). Implémentation réelle : <c>DisplaySystem</c> ; doubles dans les tests.</summary>
public interface IDisplaySystem
{
    /// <summary>Disposition active complète.</summary>
    DisplaySnapshot Save();
    /// <summary>Réapplique exactement une disposition sauvegardée. Lève une exception en cas de refus.</summary>
    void Restore(DisplaySnapshot snapshot);
    /// <summary>Dernier recours : disposition que Windows a mémorisée pour les écrans branchés.</summary>
    void ResetToDatabase();
    /// <summary>La cible de l'écran virtuel existe (pilote activé, moniteur arrivé).</summary>
    bool VirtualConnected();
    /// <summary>
    /// Allume l'écran virtuel en recopie (de <paramref name="cloneOf"/> ou du principal) ou en extension, en gardant tels
    /// quels les chemins de <paramref name="basis"/> (null = disposition actuelle). Lève une exception si c'est impossible.
    /// </summary>
    void PlaceVirtual(DisplaySnapshot? basis, TvLayout layout, string? cloneOf, int width, int height);
    /// <summary>L'écran virtuel s'il est allumé, sinon null.</summary>
    VirtualInfo? Virtual();
    IReadOnlyList<WindowDesc> Windows();
    DisplayRect PrimaryWorkArea();
    void MoveWindow(long handle, DisplayRect to);
}

/// <summary>Le périphérique du pilote (Root\MttVDD).</summary>
public interface IVirtualDevice
{
    VddStatus Status();
    bool IsEnabled();
    /// <summary>Active ou désactive le périphérique et attend que ce soit fait. Lève une exception en cas d'échec.</summary>
    void SetEnabled(bool on);
}

/// <summary>Marqueur persistant « disposition à restaurer » : écrit avant toute modification, effacé après restauration.</summary>
public interface IRestoreStore
{
    void Write(DisplaySnapshot snapshot);
    DisplaySnapshot? Read();
    void Clear();
}

/// <summary>Marqueur dans un fichier JSON (%LOCALAPPDATA%\KairoCast\display-restore.json), écrit de façon atomique.</summary>
public sealed class FileRestoreStore : IRestoreStore
{
    private readonly string _path;

    public FileRestoreStore(string path) => _path = path;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairoCast", "display-restore.json");

    public void Write(DisplaySnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
        File.Move(tmp, _path, overwrite: true);
    }

    public DisplaySnapshot? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var s = JsonSerializer.Deserialize<DisplaySnapshot>(File.ReadAllText(_path));
            return s == null || string.IsNullOrEmpty(s.Data) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); } catch { }
    }
}

/// <summary>
/// L'écran virtuel pendant un partage, sans effet en dehors :
/// <list type="bullet">
/// <item>au repos, le périphérique est désactivé : Windows (et Win+P) ne voit pas d'écran de plus ;</item>
/// <item>au début : sauvegarde de la disposition complète (et marqueur sur disque), activation, puis écran virtuel en
/// recopie de l'écran principal par-dessus la disposition sauvegardée (un clone HDMI reste un clone) ;</item>
/// <item>pendant : Win+P décide (Dupliquer, Étendre…), on capture toujours l'écran virtuel ;</item>
/// <item>à la fin (arrêt, erreur, fermeture, déconnexion, ou démarrage suivant après un plantage) : disposition
/// sauvegardée réappliquée telle quelle, périphérique désactivé, fenêtres de l'écran virtuel ramenées sur le principal.</item>
/// </list>
/// Sans appel direct à Windows : tout passe par les interfaces (testé avec des doubles).
/// </summary>
public sealed class VirtualScreen
{
    private readonly IDisplaySystem _display;
    private readonly IVirtualDevice _device;
    private readonly IRestoreStore _store;
    private readonly Action<string> _log;
    private readonly Action<TimeSpan> _sleep;
    private readonly TimeSpan _timeout;
    /// <summary>Essais de mise en recopie avant d'abandonner l'écran virtuel pour ce partage.</summary>
    public const int CloneAttempts = 6;
    private readonly object _lock = new();
    private DisplaySnapshot? _saved;
    private int _width = 1920, _height = 1080;

    public VirtualScreen(IDisplaySystem display, IVirtualDevice device, IRestoreStore store, Action<string> log,
        Action<TimeSpan>? sleep = null, TimeSpan? timeout = null)
    {
        _display = display;
        _device = device;
        _store = store;
        _log = log;
        _sleep = sleep ?? Thread.Sleep;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>Un partage utilise l'écran virtuel (disposition sauvegardée, à restaurer).</summary>
    public bool Active
    {
        get { lock (_lock) return _saved != null; }
    }

    public VddStatus Status() => _device.Status();

    /// <summary>
    /// Ajoute l'écran virtuel (recopie de <paramref name="cloneOf"/>, ou de l'écran principal) et renvoie l'écran à capturer.
    /// En cas d'échec, tout est remis comme avant et l'exception est relancée.
    /// </summary>
    public string Start(int width, int height, string? cloneOf)
    {
        lock (_lock)
        {
            if (_saved != null)
                return _display.Virtual()?.DeviceName ?? throw new InvalidOperationException("l'écran virtuel n'est pas allumé");
            var status = _device.Status();
            if (status != VddStatus.Ready)
                throw new InvalidOperationException(status == VddStatus.NotInstalled
                    ? "le pilote d'écran virtuel n'est pas installé"
                    : "le pilote d'écran virtuel n'est pas encore activé pour Kairo Cast");
            // Restes d'une session interrompue : d'abord revenir à la disposition d'origine.
            var left = _store.Read();
            if (left != null)
            {
                _log("Écran virtuel : disposition d'une session interrompue à restaurer d'abord");
                EndCore(left, disableDevice: true);
            }

            _width = width;
            _height = height;
            var snap = _display.Save();
            _store.Write(snap);
            _saved = snap;
            _log($"Écran virtuel : disposition sauvegardée : {snap.Summary}");
            try
            {
                _device.SetEnabled(true);
                if (!WaitFor(_display.VirtualConnected))
                    throw new InvalidOperationException("l'écran virtuel n'apparaît pas (le pilote ne répond pas)");
                // Juste après l'activation, Windows peut ne pas encore proposer la recopie : quelques essais.
                // Jamais d'Étendre imposé : si la recopie reste refusée, tout est remis comme avant (catch plus bas)
                // et l'appelant partage l'écran sans écran virtuel.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        _display.PlaceVirtual(snap, TvLayout.Duplicate, cloneOf, width, height);
                        break;
                    }
                    catch (Exception e) when (attempt < CloneAttempts)
                    {
                        _log($"Écran virtuel : recopie refusée (essai {attempt}/{CloneAttempts} : {e.Message}), nouvel essai");
                        _sleep(TimeSpan.FromMilliseconds(500));
                    }
                }
                VirtualInfo? v = null;
                if (!WaitFor(() => (v = _display.Virtual()) != null))
                    throw new InvalidOperationException("l'écran virtuel ne s'est pas allumé");
                _log($"Écran virtuel : allumé ({v!.Layout}) sur {v.DeviceName}");
                return v.DeviceName;
            }
            catch
            {
                EndCore(snap, disableDevice: true);
                _saved = null;
                throw;
            }
        }
    }

    /// <summary>Écran à capturer maintenant (suit Win+P), null si Windows n'affiche pas l'écran virtuel.</summary>
    public string? CaptureDevice()
    {
        lock (_lock) return _saved == null ? null : SafeVirtual()?.DeviceName;
    }

    /// <summary>Ce que montre la TV (Off hors partage).</summary>
    public TvLayout Layout()
    {
        lock (_lock) return _saved == null ? TvLayout.Off : SafeVirtual()?.Layout ?? TvLayout.Off;
    }

    /// <summary>
    /// Raccourci du panneau, comme Win+P mais pour la TV seulement : recopie (de l'écran choisi ou du principal) ou
    /// écran étendu ; les autres écrans ne bougent pas.
    /// </summary>
    public void SetLayout(TvLayout layout, string? cloneOf)
    {
        if (layout is not (TvLayout.Duplicate or TvLayout.Extend)) throw new ArgumentOutOfRangeException(nameof(layout));
        lock (_lock)
        {
            if (_saved == null) throw new InvalidOperationException("aucun partage avec l'écran virtuel");
            _display.PlaceVirtual(null, layout, cloneOf, _width, _height);
            _log($"Écran virtuel : disposition {layout} demandée depuis le panneau");
        }
    }

    /// <summary>Fin du partage : restaure la disposition sauvegardée, désactive le pilote, rapatrie les fenêtres. Ne lève jamais.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            var snap = _saved ?? _store.Read();
            _saved = null;
            if (snap == null) return;
            EndCore(snap, disableDevice: true);
        }
    }

    /// <summary>
    /// Au lancement : si une session a été interrompue (marqueur présent), restaure tout. Si le pilote est activé hors
    /// partage, le désactive. Renvoie vrai si quelque chose a été remis en ordre. Ne lève jamais.
    /// </summary>
    public bool Recover()
    {
        lock (_lock)
        {
            if (_saved != null) return false;
            var snap = _store.Read();
            if (snap != null)
            {
                _log("Écran virtuel : session interrompue (plantage ?), restauration de la disposition d'avant");
                EndCore(snap, disableDevice: true);
                return true;
            }
            try
            {
                if (_device.Status() == VddStatus.Ready && _device.IsEnabled())
                {
                    _log("Écran virtuel : pilote activé hors partage, désactivation");
                    EndCore(null, disableDevice: true);
                    return true;
                }
            }
            catch (Exception e) { _log($"Écran virtuel : vérification au lancement impossible : {e.Message}"); }
            return false;
        }
    }

    /// <summary>
    /// Protège une opération qui peut faire apparaître l'écran virtuel (installation, activation du pilote) :
    /// disposition sauvegardée avant, réappliquée après, fenêtres rapatriées.
    /// </summary>
    public T Guard<T>(Func<T> action)
    {
        lock (_lock)
        {
            var snap = _display.Save();
            _store.Write(snap);
            try { return action(); }
            finally { EndCore(snap, disableDevice: false); }
        }
    }

    private void EndCore(DisplaySnapshot? snap, bool disableDevice)
    {
        var v = SafeVirtual();
        IReadOnlyList<WindowDesc> rescue = Array.Empty<WindowDesc>();
        if (v != null && v.Layout != TvLayout.Duplicate)
        {
            try { rescue = DisplayLogic.WindowsToRescue(_display.Windows(), v.Bounds); }
            catch (Exception e) { _log($"Écran virtuel : liste des fenêtres impossible : {e.Message}"); }
        }

        var restored = snap == null || TryRestore(snap);
        if (disableDevice)
        {
            try { _device.SetEnabled(false); }
            catch (Exception e) { _log($"Écran virtuel : désactivation du pilote impossible : {e.Message}"); }
        }
        // Le départ du moniteur peut faire réappliquer à Windows une disposition mémorisée : on revérifie.
        if (snap != null && restored)
        {
            try
            {
                var now = _display.Save();
                if (now.Summary != snap.Summary)
                {
                    _log($"Écran virtuel : disposition changée par Windows ({now.Summary}), nouvelle restauration");
                    restored = TryRestore(snap);
                }
            }
            catch (Exception e) { _log($"Écran virtuel : vérification de la disposition impossible : {e.Message}"); }
        }

        if (rescue.Count > 0 && v != null)
        {
            try
            {
                var work = _display.PrimaryWorkArea();
                foreach (var w in rescue)
                {
                    try { _display.MoveWindow(w.Handle, DisplayLogic.Relocate(w.Bounds, v.Bounds, work)); }
                    catch (Exception e) { _log($"Écran virtuel : fenêtre « {w.Title} » non déplacée : {e.Message}"); }
                }
                _log($"Écran virtuel : {rescue.Count} fenêtre(s) ramenée(s) sur l'écran principal");
            }
            catch (Exception e) { _log($"Écran virtuel : rapatriement des fenêtres impossible : {e.Message}"); }
        }

        if (snap == null) return;
        if (restored) _store.Clear();
        else _log("Écran virtuel : disposition non restaurée, nouvel essai au prochain lancement (sinon Win+P › Dupliquer)");
    }

    private bool TryRestore(DisplaySnapshot snap)
    {
        try
        {
            _display.Restore(snap);
            _log($"Écran virtuel : disposition restaurée : {snap.Summary}");
            return true;
        }
        catch (Exception e)
        {
            _log($"Écran virtuel : restauration exacte refusée ({e.Message}), disposition mémorisée par Windows");
        }
        try
        {
            _display.ResetToDatabase();
            return true;
        }
        catch (Exception e)
        {
            _log($"Écran virtuel : disposition mémorisée refusée aussi : {e.Message}");
            return false;
        }
    }

    private VirtualInfo? SafeVirtual()
    {
        try { return _display.Virtual(); }
        catch (Exception e) { _log($"Écran virtuel : état illisible : {e.Message}"); return null; }
    }

    private bool WaitFor(Func<bool> condition)
    {
        var step = TimeSpan.FromMilliseconds(200);
        for (var waited = TimeSpan.Zero; ; waited += step)
        {
            bool ok;
            try { ok = condition(); } catch { ok = false; }
            if (ok) return true;
            if (waited >= _timeout) return false;
            _sleep(step);
        }
    }
}
