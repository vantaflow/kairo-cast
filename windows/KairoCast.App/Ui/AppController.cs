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
    private readonly EarlyEndGate<StreamSession> _gate = new();
    private readonly Dictionary<(System.Net.IPAddress, int), BoxStatus?> _status = new();
    private readonly CancellationTokenSource _pollCts = new();

    public AppSettings Settings { get; }
    /// <summary>Box à afficher : manuelles, et découvertes sur un réseau actuel du PC.</summary>
    public IReadOnlyList<BoxInfo> Boxes => BoxDisplay.Visible(_boxes, IsManual, BoxDisplay.LocalNetworks());

    /// <summary>Dernier état connu de la box (libre / occupée par …) ; null si inconnu ou box trop ancienne.</summary>
    public BoxStatus? StatusOf(BoxInfo b) => _status.TryGetValue((b.Address, b.Port), out var s) ? s : null;

    /// <summary>Interroge les box visibles toutes les quelques secondes (sauf celle qu'on partage).</summary>
    private async Task PollStatusAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var boxes = Boxes.Where(b => !IsActive(b)).ToList();
                var results = await Task.WhenAll(boxes.Select(async b =>
                    (b, await BoxStatus.QueryAsync(new System.Net.IPEndPoint(b.Address, b.Port), TimeSpan.FromSeconds(2), ct))));
                _ui.Post(_ =>
                {
                    var changed = false;
                    foreach (var (b, st) in results)
                    {
                        var key = (b.Address, b.Port);
                        if (!_status.TryGetValue(key, out var old) || old != st) { _status[key] = st; changed = true; }
                    }
                    if (changed) Changed?.Invoke();
                }, null);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Warn("État des box : " + e.Message); try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { return; } }
        }
    }

    public bool IsManual(BoxInfo b) => _manual.Any(m => Same(m, b));

    /// <summary>Retire une box ajoutée à la main (liste et réglages).</summary>
    public void RemoveManual(BoxInfo b)
    {
        if (IsActive(b)) return;
        _manual.RemoveWhere(m => Same(m, b));
        _boxes.RemoveAll(x => Same(x, b));
        var plain = b.Address.ToString();
        var withPort = $"{b.Address}:{b.Port}";
        Settings.ManualBoxes.RemoveAll(e => e == withPort || (e == plain && b.Port == ManualAddress.DefaultPort));
        Settings.Save();
        Changed?.Invoke();
    }
    public BoxInfo? Active { get; private set; }
    public bool Busy { get; private set; }
    public string EncoderText { get; private set; } = "";
    public event Action? Changed;

    public AppController(SynchronizationContext ui, AppSettings settings)
    {
        _ui = ui;
        Settings = settings;
    }

    public string State => _sessionState ?? (Boxes.Count == 0 ? _discoveryState : "Prêt à partager");

    public void Start()
    {
        // Changement de réseau : la liste visible change (box de l'ancien réseau masquées).
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => _ui.Post(_ => Changed?.Invoke(), null);
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
        _ = Task.Run(() => PollStatusAsync(_pollCts.Token));
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
        _gate.Reset();
        try
        {
            var s = await StreamSession.StartAsync(box, Settings.ToStreamOptions(), cts.Token,
                onEnded: e => _ui.Post(_ => _gate.Ended(e, OnEnded), null),
                onStatus: st => _ui.Post(_ =>
                {
                    if (_gate.Session != null && _session == _gate.Session) { _sessionState = st; Changed?.Invoke(); }
                }, null));
            _session = s;
            if (_gate.Started(s, out var early)) { OnEnded(s, early); return; }
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
        _pollCts.Cancel();
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
