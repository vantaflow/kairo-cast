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
    /// <summary>La session en cours (ou en démarrage) utilise l'écran virtuel.</summary>
    private bool _virtualOn;
    private TvLayout _tvLayout = TvLayout.Off;
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

    /// <summary>
    /// Demande à l'utilisateur s'il accepte d'installer (NotInstalled) ou d'activer (NeedsSetup) l'écran virtuel.
    /// Fil de l'interface. Jamais appelé sans clic de l'utilisateur.
    /// </summary>
    public Func<VddStatus, bool>? ConfirmVirtualDisplaySetup { get; set; }

    /// <summary>État du pilote d'écran virtuel ; null tant qu'il n'a pas été lu.</summary>
    public VddStatus? VirtualStatus { get; private set; }

    /// <summary>Installation ou activation du pilote en cours.</summary>
    public bool VirtualSetupRunning { get; private set; }

    /// <summary>Le prochain partage utilisera l'écran virtuel (TV = écran de Windows, Win+P).</summary>
    public bool UsesVirtualDisplay => Settings.UseVirtualDisplay && VirtualStatus == VddStatus.Ready;

    /// <summary>Ce que Windows affiche sur la TV pendant un partage avec l'écran virtuel (Off sinon).</summary>
    public TvLayout TvLayout => _virtualOn && _session != null ? _tvLayout : TvLayout.Off;

    /// <summary>Écrans réels allumés, proposés comme écran à recopier (le principal d'abord).</summary>
    public IReadOnlyList<DisplayInfo> Screens()
    {
        try { return DisplayLogic.DuplicateCandidates(DisplayNative.List()); }
        catch (Exception e) { Log.Warn($"Liste des écrans : {Log.Describe(e)}"); return Array.Empty<DisplayInfo>(); }
    }

    /// <summary>Active ou non l'écran virtuel pour les prochains partages.</summary>
    public void SetUseVirtualDisplay(bool on)
    {
        if (Settings.UseVirtualDisplay == on) return;
        Settings.UseVirtualDisplay = on;
        Settings.Save();
        Log.Info($"Écran virtuel pour la TV : {(on ? "oui" : "non")}");
        Changed?.Invoke();
    }

    /// <summary>Change l'écran recopié (null = principal). Pris en compte au prochain partage.</summary>
    public void SetDuplicateScreen(string? deviceName)
    {
        if (Settings.DuplicateScreen == deviceName) return;
        Settings.DuplicateScreen = deviceName;
        Settings.Save();
        Log.Info($"Écran à recopier : {deviceName ?? "principal"}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Raccourci du panneau pendant un partage : la TV recopie l'écran (Dupliquer) ou devient un écran à part (Étendre).
    /// Comme Win+P, mais seule la TV change : un clone HDMI existant n'est pas touché.
    /// </summary>
    public async Task SetTvLayoutAsync(TvLayout layout)
    {
        if (TvLayout == TvLayout.Off || TvLayout == layout) return;
        var screen = Settings.DuplicateScreen;
        try { await Task.Run(() => VirtualDisplay.Screen.SetLayout(layout, screen)); }
        catch (Exception e)
        {
            Log.Error($"Écran virtuel : disposition {layout} refusée", e);
            _sessionState = $"Windows refuse ce mode d'affichage : {e.Message}";
        }
        OnDisplaysChanged();
    }

    /// <summary>Relit l'état du pilote (lecture seule, hors du fil de l'interface).</summary>
    public async Task RefreshVirtualStatusAsync()
    {
        try { VirtualStatus = await Task.Run(VirtualDisplay.Screen.Status); }
        catch (Exception e) { Log.Warn($"Écran virtuel : état illisible : {Log.Describe(e)}"); }
        Changed?.Invoke();
    }

    /// <summary>Installe ou active le pilote, sur clic de l'utilisateur et avec son accord (UAC).</summary>
    public async Task SetupVirtualDisplayAsync()
    {
        if (VirtualSetupRunning || Busy || _session != null) return;
        var status = VirtualStatus ?? VddStatus.NotInstalled;
        if (status == VddStatus.Ready) return;
        if (ConfirmVirtualDisplaySetup?.Invoke(status) != true) return;
        VirtualSetupRunning = true;
        Changed?.Invoke();
        void Status(string st) => _ui.Post(_ => { _sessionState = st; Changed?.Invoke(); }, null);
        string? err;
        try { err = await Task.Run(() => VirtualDisplay.Setup(Status)); }
        catch (Exception e) { Log.Error("Écran virtuel : installation impossible", e); err = $"Installation impossible : {e.Message}"; }
        VirtualSetupRunning = false;
        await RefreshVirtualStatusAsync();
        _sessionState = err ?? (VirtualStatus == VddStatus.Ready
            ? "Écran virtuel prêt : pendant un partage, Win+P choisit Dupliquer ou Étendre"
            : "Le pilote n'est pas prêt (voir le journal)");
        Changed?.Invoke();
    }

    private void OnDisplaysChanged()
    {
        var s = _session;
        if (_virtualOn && s != null)
        {
            Task.Run(() =>
            {
                s.FollowTarget();
                var layout = VirtualDisplay.Screen.Layout();
                _ui.Post(_ => { _tvLayout = layout; Changed?.Invoke(); }, null);
            });
        }
        else Changed?.Invoke();
    }

    public AppController(SynchronizationContext ui, AppSettings settings)
    {
        _ui = ui;
        Settings = settings;
    }

    public string State => _sessionState ?? (Boxes.Count == 0 ? _discoveryState : "Prêt à partager");

    public void Start()
    {
        // Plantage pendant un partage avec l'écran virtuel : disposition d'avant restaurée (marqueur sur disque).
        _ = Task.Run(() =>
        {
            try { VirtualDisplay.Screen.Recover(); }
            catch (Exception e) { Log.Warn($"Écran virtuel : remise en ordre au lancement : {Log.Describe(e)}"); }
        }).ContinueWith(_ => _ui.Post(_ => _ = RefreshVirtualStatusAsync(), null), TaskScheduler.Default);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _ui.Post(_ => OnDisplaysChanged(), null);
        // Fermeture de session ou arrêt de Windows pendant un partage : la disposition est restaurée tout de suite.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
        {
            try { VirtualDisplay.EmergencyStop(); } catch (Exception e) { Log.Warn($"Écran virtuel : {Log.Describe(e)}"); }
        };
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
            var opt = Settings.ToStreamOptions();
            var target = await PrepareTargetAsync(opt, cts.Token);
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
    /// Écran à capturer. Avec l'écran virtuel : disposition sauvegardée, TV ajoutée en recopie, puis Win+P décide ;
    /// la capture suit toujours la TV. Sans : l'écran choisi ou le principal, sans toucher à l'affichage.
    /// </summary>
    private async Task<CaptureTarget> PrepareTargetAsync(Media.StreamOptions opt, CancellationToken ct)
    {
        if (UsesVirtualDisplay)
        {
            _sessionState = "Ajout de l'écran de la TV…";
            Changed?.Invoke();
            _virtualOn = true;
            var screen = Settings.DuplicateScreen;
            var vs = VirtualDisplay.Screen;
            try
            {
                var device = await Task.Run(() => vs.Start(opt.Width, opt.Height, screen), ct);
                _tvLayout = await Task.Run(vs.Layout);
                return new CaptureTarget(device, true, vs.CaptureDevice);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // L'affichage a été remis comme avant : partage classique, sans toucher aux écrans.
                Log.Warn($"Écran virtuel : indisponible pour ce partage ({Log.Describe(e)}), recopie de l'écran sans écran virtuel");
                _virtualOn = false;
                _tvLayout = TvLayout.Off;
            }
        }
        if (string.IsNullOrEmpty(Settings.DuplicateScreen)) return CaptureTarget.Primary;
        var name = DisplayLogic.DuplicateTarget(DisplayNative.List(), Settings.DuplicateScreen);
        return name == null ? CaptureTarget.Primary : new CaptureTarget(name, false);
    }

    /// <summary>Retire l'écran virtuel s'il a servi à ce partage : disposition d'avant, fenêtres ramenées.</summary>
    private async Task TurnOffVirtualAsync()
    {
        if (!_virtualOn) return;
        _virtualOn = false;
        _tvLayout = TvLayout.Off;
        try { await Task.Run(VirtualDisplay.Screen.Stop); }
        catch (Exception e) { Log.Warn($"Écran virtuel : arrêt : {Log.Describe(e)}"); }
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
        _pollCts.Cancel();
        _startCts?.Cancel();
        var s = _session;
        _session = null;
        if (s != null)
        {
            try { Task.Run(() => s.DisposeAsync().AsTask()).Wait(5000); } catch { }
        }
        // Fermeture de l'appli : disposition d'avant le partage, tout de suite (pas d'attente du fil de l'interface).
        _virtualOn = false;
        try { VirtualDisplay.EmergencyStop(); } catch (Exception e) { Log.Warn($"Écran virtuel : {Log.Describe(e)}"); }

        try { _discovery?.Dispose(); } catch { }
    }
}
