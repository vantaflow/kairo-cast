using System.Reflection;
using KairoCast.App.Diagnostics;
using KairoCast.App.Shell;

namespace KairoCast.App.Ui;

/// <summary>Application de la zone de notification : icône, menu, fenêtre, panneau Win+K, crochet clavier.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly AppController _controller;
    private readonly MainForm _main;
    private readonly CastPanel _panel;
    private readonly KeyboardHook _hook;
    private readonly NotifyIcon _tray;
    private readonly Icon? _icon;
    private readonly EventWaitHandle _showSignal;
    private RegisteredWaitHandle? _wait;
    private bool _exiting;

    public TrayApp(bool showWindow, EventWaitHandle showSignal)
    {
        _showSignal = showSignal;
        _settings = AppSettings.Load();
        _icon = LoadIcon();
        // Un contrôle créé sur ce fil installe le contexte de synchronisation WinForms.
        using (var anchor = new Control()) _ = anchor.Handle;
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _controller = new AppController(ui, _settings);
        _controller.ConfirmVirtualDisplaySetup = status => MessageBox.Show(
            "Avec un écran virtuel, la TV devient un écran de Windows pendant le partage : Win+P choisit Dupliquer " +
            "(par défaut) ou Étendre, comme pour un écran branché. Hors partage, il n'existe pas pour Windows.\n\n" +
            (status == Display.VddStatus.NotInstalled
                ? "Il faut installer une seule fois le pilote open source « Virtual Display Driver » (VirtualDrivers, " +
                  "licence MIT, signé) : téléchargement par winget, puis Windows demande une autorisation.\n\n"
                : "Le pilote « Virtual Display Driver » est installé ; Kairo Cast doit pouvoir l'activer au début d'un partage " +
                  "et le désactiver à la fin. Windows demande une autorisation, une seule fois.\n\n") +
            "L'affichage actuel est sauvegardé avant et remis tel quel après.\n\nContinuer ?",
            "Kairo Cast – écran virtuel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        _main = new MainForm(_controller, _icon);
        _ = _main.Handle;
        _panel = new CastPanel(_controller);
        _ = _panel.Handle;

        _hook = new KeyboardHook(ui) { ReplaceWinK = _settings.ReplaceWinK };
        _hook.PanelRequested += () => _panel.Toggle();
        _panel.OthersRequested += OpenNativePanel;
        _main.ReplaceWinKChanged += v => _hook.ReplaceWinK = v;

        _tray = new NotifyIcon { Icon = _icon ?? SystemIcons.Application, Text = "Kairo Cast", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _tray.DoubleClick += (_, _) => _main.ShowAndFront();

        Autostart.Apply(_settings.AutoStart);
        _controller.Start();

        _wait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => ui.Post(_ => _main.ShowAndFront(), null), null, Timeout.Infinite, false);
        if (showWindow) _main.ShowAndFront();
    }

    /// <summary>À appeler en dernier, juste avant Application.Run.</summary>
    public void InstallHook() => _hook.Install();

    private static Icon? LoadIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KairoCast.ico");
            return s == null ? null : new Icon(s);
        }
        catch { return null; }
    }

    private void OpenNativePanel()
    {
        if (_hook.Installed && _hook.SendNativeWinK()) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings-connectabledevices:devicediscovery") { UseShellExecute = true }); }
        catch (Exception e) { Log.Warn("Panneau Windows : " + e.Message); }
    }

    private void BuildMenu()
    {
        var m = _tray.ContextMenuStrip!;
        m.Items.Clear();
        m.Items.Add("Ouvrir Kairo Cast", null, (_, _) => _main.ShowAndFront());
        m.Items.Add(new ToolStripSeparator());
        foreach (var box in _controller.Boxes.ToList())
        {
            var b = box;
            var active = _controller.IsActive(b);
            m.Items.Add(active ? $"Partage vers {b.Name} en cours" : $"Partager vers {b.Name}", null,
                async (_, _) => { try { await _controller.ShareAsync(b); } catch (Exception e) { Log.Error("Partage", e); } }).Enabled = !active && !_controller.Busy;
        }
        if (_controller.Boxes.Count == 0) m.Items.Add("Aucune box trouvée").Enabled = false;
        m.Items.Add("Arrêter le partage", null,
            async (_, _) => { try { await _controller.StopAsync(); } catch (Exception e) { Log.Error("Arrêt", e); } }).Enabled = _controller.Active != null;
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quitter", null, (_, _) => Quit());
    }

    private void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        _wait?.Unregister(null);
        _hook.Dispose();
        _tray.Visible = false;
        _controller.Dispose();
        _main.AllowClose = true;
        _panel.Dispose();
        _main.Close();
        _tray.Dispose();
        ExitThread();
    }
}
