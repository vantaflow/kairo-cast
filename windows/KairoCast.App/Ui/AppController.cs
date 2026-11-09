using KairoCast.App.Capture;
using KairoCast.App.Diagnostics;
using KairoCast.App.Display;
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
    /// <summary>L'écran virtuel a été allumé pour la session en cours (ou en démarrage).</summary>
    private bool _virtualOn;

    public AppSettings Settings { get; }
    /// <summary>Box à afficher : manuelles, et découvertes sur un réseau actuel du PC.</summary>
    public IReadOnlyList<BoxInfo> Boxes => BoxDisplay.Visible(_boxes, IsManual, BoxDisplay.LocalNetworks());

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

    /// <summary>Demande à l'utilisateur s'il accepte d'installer le pilote d'écran virtuel (mode Étendre). Fil de l'interface.</summary>
    public Func<bool>? ConfirmVirtualDisplayInstall { get; set; }

    /// <summary>Écrans réels allumés, proposés en mode Dupliquer (le principal d'abord).</summary>
    public IReadOnlyList<DisplayInfo> Screens()
    {
        try { return DisplayLogic.DuplicateCandidates(DisplayNative.List()); }
        catch (Exception e) { Log.Warn($"Liste des écrans : {Log.Describe(e)}"); return Array.Empty<DisplayInfo>(); }
    }

    /// <summary>Change le mode ; un partage en cours redémarre aussitôt dans le nouveau mode.</summary>
    public void SetShareMode(ShareMode mode)
    {
        if (Settings.ShareMode == mode) return;
        Settings.ShareMode = mode;
        Settings.Save();
        Log.Info($"Mode de partage : {mode}");
        RestartIfActive();
        Changed?.Invoke();
    }

    /// <summary>Change l'écran recopié en mode Dupliquer (null = principal).</summary>
    public void SetDuplicateScreen(string? deviceName)
    {
        if (Settings.DuplicateScreen == deviceName) return;
        Settings.DuplicateScreen = deviceName;
        Settings.Save();
        Log.Info($"Écran à recopier : {deviceName ?? "principal"}");
        if (Settings.ShareMode == ShareMode.Duplicate) RestartIfActive();
        Changed?.Invoke();
    }

    private void RestartIfActive()
    {
        var box = Active;
        if (box != null && !Busy && _session != null) _ = ShareAsync(box);
    }

    public AppController(SynchronizationContext ui, AppSettings settings)
    {
        _ui = ui;
        Settings = settings;
    }

    public string State => _sessionState ?? (Boxes.Count == 0 ? _discoveryState : "Prêt à partager");

    public void Start()
    {
        // Arrêt brutal pendant un partage Étendre : l'écran virtuel est resté allumé, on l'éteint.
        if (Settings.VirtualDisplayOn)
        {
            Log.Info("Écran virtuel resté allumé par une session précédente : extinction");
            Task.Run(VirtualDisplay.Deactivate);
            Settings.VirtualDisplayOn = false;
            Settings.Save();
        }
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
            var opt = Settings.ToStreamOptions();
            var target = await PrepareTargetAsync(opt, cts.Token);
            if (target == null)
            {
                Active = null;
                return;
            }
            var s = await StreamSession.StartAsync(box, opt, cts.Token, target: target,
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
            await TurnOffVirtualAsync();
        }
        catch (Exception e)
        {
            Log.Error("Partage impossible", e);
            Active = null;
            _sessionState = $"Partage impossible : {e.Message}";
            await TurnOffVirtualAsync();
        }
        finally
        {
            Busy = false;
            _startCts = null;
            cts.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Écran à capturer. Dupliquer : l'écran choisi ou le principal. Étendre : installe le pilote au besoin (avec accord),
    /// puis allume l'écran virtuel à la définition envoyée à la box. null = partage abandonné (état déjà renseigné).
    /// </summary>
    private async Task<CaptureTarget?> PrepareTargetAsync(Media.StreamOptions opt, CancellationToken ct)
    {
        if (Settings.ShareMode == ShareMode.Duplicate)
        {
            if (string.IsNullOrEmpty(Settings.DuplicateScreen)) return CaptureTarget.Primary;
            var name = DisplayLogic.DuplicateTarget(DisplayNative.List(), Settings.DuplicateScreen);
            return name == null ? CaptureTarget.Primary : new CaptureTarget(name, false);
        }

        var (state, _) = await Task.Run(VirtualDisplay.Detect, ct);
        if (state == VddState.NotInstalled)
        {
            if (ConfirmVirtualDisplayInstall?.Invoke() != true)
            {
                _sessionState = "Mode Étendre : le pilote d'écran virtuel n'est pas installé";
                return null;
            }
            void Status(string st) => _ui.Post(_ => { _sessionState = st; Changed?.Invoke(); }, null);
            var err = await Task.Run(() => VirtualDisplay.Install(Status), ct);
            if (err != null)
            {
                _sessionState = err;
                return null;
            }
        }
        _sessionState = "Allumage de l'écran virtuel…";
        Changed?.Invoke();
        _virtualOn = true;
        Settings.VirtualDisplayOn = true;
        Settings.Save();
        var device = await Task.Run(() => VirtualDisplay.Activate(opt.Width, opt.Height), ct);
        return new CaptureTarget(device, true);
    }

    /// <summary>Éteint l'écran virtuel s'il a été allumé pour ce partage (les fenêtres reviennent sur l'écran principal).</summary>
    private async Task TurnOffVirtualAsync()
    {
        if (!_virtualOn) return;
        _virtualOn = false;
        await Task.Run(VirtualDisplay.Deactivate);
        Settings.VirtualDisplayOn = false;
        Settings.Save();
    }

    private void OnEnded(StreamSession s, Exception? e)
    {
        if (_session != s) return;
        _session = null;
        Active = null;
        EncoderText = "";
        _sessionState = e == null ? "Partage terminé" : $"Partage interrompu : {s.Status}";
        _ = DisposeThenTurnOff(s);
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
        await DisposeThenTurnOff(s);
    }

    private async Task DisposeThenTurnOff(StreamSession s)
    {
        await DisposeQuietly(s);
        await TurnOffVirtualAsync();
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
        if (_virtualOn || Settings.VirtualDisplayOn)
        {
            _virtualOn = false;
            VirtualDisplay.Deactivate();
            Settings.VirtualDisplayOn = false;
            Settings.Save();
        }

        try { _discovery?.Dispose(); } catch { }
    }
}
