using KairoCast.App.Diagnostics;
using KairoCast.App.Shell;
using KairoCast.App.Streaming;
using KairoCast.Link;

namespace KairoCast.App.Ui;

/// <summary>
/// État commun du panneau, de la fenêtre et du menu de la zone de notification : box trouvées, session en cours, état.
/// Tout est lu et modifié sur le fil de l'interface ; les événements de la découverte et de la session y sont renvoyés.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly List<BoxInfo> _boxes = new();
    private readonly HashSet<BoxInfo> _manual = new();
    private Discovery? _discovery;
    private StreamSession? _session;
    private CancellationTokenSource? _startCts;
    private string _discoveryState = "Recherche des box…";
    private string? _sessionState;
    private bool _endedEarly;
    private Exception? _earlyEnd;

    public AppSettings Settings { get; }
    public IReadOnlyList<BoxInfo> Boxes => _boxes;
    public BoxInfo? Active { get; private set; }
    public bool Busy { get; private set; }
    public string EncoderText { get; private set; } = "";
    public event Action? Changed;

    public AppController(SynchronizationContext ui, AppSettings settings)
    {
        _ui = ui;
        Settings = settings;
    }

    public string State => _sessionState ?? (_boxes.Count == 0 ? _discoveryState : "Prêt à partager");

    public void Start()
    {
        foreach (var m in Settings.ManualBoxes.ToList())
            if (ManualAddress.TryParse(m, out var ip, out var port)) AddBox(new BoxInfo(ip.ToString(), ip, port), manual: true);
        try
        {
            var d = new Discovery();
            d.Log += s => Log.Info("Découverte : " + s);
            d.Found += b => _ui.Post(_ => { AddBox(b, manual: false); Changed?.Invoke(); }, null);
            d.Lost += b => _ui.Post(_ =>
            {
                if (!_manual.Contains(b)) _boxes.RemoveAll(x => Same(x, b));
                Changed?.Invoke();
            }, null);
            d.StatusChanged += st => _ui.Post(_ =>
            {
                _discoveryState = st switch
                {
                    DiscoveryStatus.Ok => "Recherche des box…",
                    DiscoveryStatus.NoInterface => "Aucun réseau détecté",
                    DiscoveryStatus.Error => "Recherche des box impossible (voir le journal)",
                    _ => "Recherche arrêtée",
                };
                Changed?.Invoke();
            }, null);
            _discovery = d;
            d.Start();
        }
        catch (Exception e)
        {
            Log.Error("Découverte : démarrage impossible", e);
            _discoveryState = "Recherche des box impossible (voir le journal)";
        }
        // Pare-feu : une seule fois, hors du fil de l'interface (peut demander l'élévation UAC).
        if (!Settings.FirewallAsked)
        {
            Task.Run(() =>
            {
                Firewall.EnsureRule(Log.Warn);
                Settings.FirewallAsked = true;
                Settings.Save();
            });
        }
        Changed?.Invoke();
    }

    private static bool Same(BoxInfo a, BoxInfo b) => a.Address.Equals(b.Address) && a.Port == b.Port;

    private void AddBox(BoxInfo b, bool manual)
    {
        var i = _boxes.FindIndex(x => Same(x, b));
        if (i >= 0) { if (!manual) _boxes[i] = b; }
        else _boxes.Add(b);
        if (manual) _manual.Add(_boxes.First(x => Same(x, b)));
    }

    /// <summary>Ajoute une box par son adresse IP. Renvoie un message d'erreur, ou null.</summary>
    public string? AddManual(string text)
    {
        if (!ManualAddress.TryParse(text, out var ip, out var port))
            return "Adresse invalide : saisissez par exemple 192.168.1.20 ou 192.168.1.20:7300.";
        AddBox(new BoxInfo(ip.ToString(), ip, port), manual: true);
        var entry = port == ManualAddress.DefaultPort ? ip.ToString() : $"{ip}:{port}";
        if (Settings.AddManualBox(entry)) Settings.Save();
        Changed?.Invoke();
        return null;
    }

    public bool IsActive(BoxInfo b) => Active != null && Same(Active, b);

    public async Task ShareAsync(BoxInfo box)
    {
        if (Busy) return;
        if (_session != null || Active != null) await StopAsync();
        Busy = true;
        Active = box;
        _sessionState = $"Connexion à {box.Name}…";
        EncoderText = "";
        Changed?.Invoke();
        var cts = _startCts = new CancellationTokenSource();
        try
        {
            StreamSession? self = null;
            var s = await StreamSession.StartAsync(box, Settings.ToStreamOptions(), cts.Token,
                onEnded: e => _ui.Post(_ => { if (self != null) OnEnded(self, e); else _earlyEnd = e; _endedEarly = true; }, null),
                onStatus: st => _ui.Post(_ =>
                {
                    if (self != null && _session == self) { _sessionState = st; Changed?.Invoke(); }
                }, null));
            self = s;
            _session = s;
            if (_endedEarly) { _endedEarly = false; var e = _earlyEnd; _earlyEnd = null; OnEnded(s, e); return; }
            EncoderText = $"Encodeur : {(s.UsingHardwareEncoder ? "matériel" : "logiciel")} ({s.EncoderName})" + (s.AudioActive ? "" : ", sans son");
            _sessionState = s.Status;
        }
        catch (OperationCanceledException)
        {
            Active = null;
            _sessionState = "Partage annulé";
        }
        catch (Exception e)
        {
            Log.Error("Partage impossible", e);
            Active = null;
            _sessionState = $"Partage impossible : {e.Message}";
        }
        finally
        {
            Busy = false;
            _startCts = null;
            cts.Dispose();
            Changed?.Invoke();
        }
    }

    private void OnEnded(StreamSession s, Exception? e)
    {
        if (_session != s) return;
        _session = null;
        Active = null;
        EncoderText = "";
        _sessionState = e == null ? "Partage terminé" : $"Partage interrompu : {s.Status}";
        _ = DisposeQuietly(s);
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (Busy) { _startCts?.Cancel(); return; }
        var s = _session;
        if (s == null) return;
        _session = null;
        Active = null;
        EncoderText = "";
        _sessionState = "Partage arrêté";
        Changed?.Invoke();
        await DisposeQuietly(s);
    }

    private static async Task DisposeQuietly(StreamSession s)
    {
        try { await s.DisposeAsync(); }
        catch (Exception e) { Log.Warn("Arrêt de la session : " + e.Message); }
    }

    public void Dispose()
    {
        _startCts?.Cancel();
        var s = _session;
        _session = null;
        if (s != null)
        {
            try { Task.Run(() => s.DisposeAsync().AsTask()).Wait(5000); } catch { }
        }
        try { _discovery?.Dispose(); } catch { }
    }
}
