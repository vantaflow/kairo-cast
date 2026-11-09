using KairoCast.App.Shell;
using KairoCast.Media;

namespace KairoCast.App.Ui;

/// <summary>Fenêtre principale : box, adresse manuelle, écran de la TV (écran virtuel, écran recopié), qualité, son, raccourci, démarrage avec Windows, état.</summary>
public sealed class MainForm : Form
{
    private static readonly (string Label, QualityPreset Preset)[] Qualities =
    {
        ("1080p 30 i/s (par défaut)", QualityPreset.P1080F30),
        ("1080p 60 i/s", QualityPreset.P1080F60),
        ("1440p 30 i/s", QualityPreset.P1440F30),
    };

    private readonly AppController _c;
    private readonly Label _status = new() { AutoSize = false };
    private readonly Label _encoder = new() { AutoSize = false, ForeColor = SystemColors.GrayText };
    private readonly CheckBox _useVirtual = new() { Text = "La TV devient un écran de Windows pendant le partage (Win+P : Dupliquer / Étendre)", AutoSize = true };
    private readonly Label _vddState = new() { AutoSize = false, ForeColor = SystemColors.GrayText };
    private readonly Button _vddSetup = new() { AutoSize = true, Visible = false };
    private readonly Label _screenLabel = new() { Text = "Écran à recopier :", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly ComboBox _screen = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 0, 0) };
    private List<string?> _screenNames = new();
    private bool _loading;

    /// <summary>Faux tant que l'utilisateur ferme la fenêtre (elle se réduit alors dans la zone de notification).</summary>
    public bool AllowClose { get; set; }

    public MainForm(AppController controller, Icon? icon)
    {
        _c = controller;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _status.Size = new Size(LogicalToDeviceUnits(400), LogicalToDeviceUnits(40));
        _encoder.Size = new Size(LogicalToDeviceUnits(400), LogicalToDeviceUnits(20));
        Text = "Kairo Cast";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };

        flow.Controls.Add(Heading("Box Kairo"));
        flow.Controls.Add(new BoxListControl(_c, 400, ForeColor));

        var ip = new TextBox { Width = LogicalToDeviceUnits(250), PlaceholderText = "192.168.1.20" };
        var add = new Button { Text = "Ajouter par adresse IP", AutoSize = true };
        var addError = new Label { AutoSize = false, Width = LogicalToDeviceUnits(400), Height = LogicalToDeviceUnits(20), ForeColor = Color.Firebrick };
        void DoAdd()
        {
            var err = _c.AddManual(ip.Text);
            addError.Text = err ?? "";
            if (err == null) ip.Clear();
        }
        add.Click += (_, _) => DoAdd();
        ip.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoAdd(); } };
        var addRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 6, 0, 0), Visible = false };
        addRow.Controls.AddRange(new Control[] { ip, add });
        // Secours seulement : la box apparaît d'elle-même dans la liste ; l'adresse ne sert que si la
        // découverte est bloquée (VPN, routeur sans multicast, autre sous-réseau).
        var manualLink = new LinkLabel { Text = "Box introuvable ? Ajouter par adresse IP", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        manualLink.LinkClicked += (_, _) => { manualLink.Visible = false; addRow.Visible = true; ip.Focus(); };
        flow.Controls.Add(manualLink);
        flow.Controls.Add(addRow);
        flow.Controls.Add(addError);

        flow.Controls.Add(Heading("Écran de la TV"));
        _screen.Width = LogicalToDeviceUnits(250);
        _vddState.Size = new Size(LogicalToDeviceUnits(400), LogicalToDeviceUnits(52));
        flow.Controls.AddRange(new Control[] { _useVirtual, _vddState, _vddSetup, _screenLabel, _screen });

        flow.Controls.Add(Heading("Qualité"));
        var quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(250) };
        foreach (var q in Qualities) quality.Items.Add(q.Label);
        flow.Controls.Add(quality);

        var audio = new CheckBox { Text = "Envoyer le son", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        var replace = new CheckBox { Text = "Remplacer Win+K (sinon Win+Alt+K)", AutoSize = true };
        var autostart = new CheckBox { Text = "Démarrer avec Windows", AutoSize = true };
        flow.Controls.AddRange(new Control[] { audio, replace, autostart });

        flow.Controls.Add(Heading("État"));
        flow.Controls.Add(_status);
        flow.Controls.Add(_encoder);
        Controls.Add(flow);

        var s = _c.Settings;
        _loading = true;
        quality.SelectedIndex = Math.Max(0, Array.FindIndex(Qualities, q => q.Preset == s.Quality));
        audio.Checked = s.SendAudio;
        replace.Checked = s.ReplaceWinK;
        autostart.Checked = s.AutoStart;
        _loading = false;

        quality.SelectedIndexChanged += (_, _) => Change(() => s.Quality = Qualities[Math.Max(0, quality.SelectedIndex)].Preset);
        audio.CheckedChanged += (_, _) => Change(() => s.SendAudio = audio.Checked);
        replace.CheckedChanged += (_, _) => Change(() => { s.ReplaceWinK = replace.Checked; ReplaceWinKChanged?.Invoke(s.ReplaceWinK); });
        autostart.CheckedChanged += (_, _) => Change(() => { s.AutoStart = autostart.Checked; Autostart.Apply(s.AutoStart); });

        _useVirtual.CheckedChanged += (_, _) => { if (!_loading) _c.SetUseVirtualDisplay(_useVirtual.Checked); };
        // Installation ou activation du pilote : seulement sur ce clic (puis confirmation et demande UAC).
        _vddSetup.Click += async (_, _) => await _c.SetupVirtualDisplayAsync();
        _screen.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _screen.SelectedIndex < 0 || _screen.SelectedIndex >= _screenNames.Count) return;
            _c.SetDuplicateScreen(_screenNames[_screen.SelectedIndex]);
        };
        _c.Changed += Refresh_;
        Refresh_();
    }

    /// <summary>Écran virtuel (état du pilote) et liste des écrans à recopier (affichée seulement s'il y en a plusieurs).</summary>
    private void RefreshMode()
    {
        if (IsDisposed) return;
        var s = _c.Settings;
        _loading = true;
        try
        {
            _useVirtual.Checked = s.UseVirtualDisplay;
            var st = _c.VirtualStatus;
            _vddState.Text = !s.UseVirtualDisplay
                ? "Désactivé : la TV recopie l'écran choisi, sans changer l'affichage de Windows."
                : st switch
                {
                    null => "Vérification du pilote d'écran virtuel…",
                    Display.VddStatus.Ready => "Prêt. Pendant un partage, Win+P choisit ce que montre la TV (Dupliquer par défaut). " +
                                               "Hors partage, l'écran virtuel n'existe pas pour Windows.",
                    Display.VddStatus.NeedsSetup => "Le pilote « Virtual Display Driver » est installé mais pas encore activé pour Kairo Cast " +
                                                    "(une autorisation Windows). En attendant, la TV recopie l'écran choisi.",
                    _ => "Le pilote d'écran virtuel « Virtual Display Driver » n'est pas installé : la TV recopie l'écran choisi. " +
                         "Installez-le pour utiliser Win+P (Dupliquer / Étendre) avec la TV.",
                };
            _vddSetup.Visible = s.UseVirtualDisplay && st is Display.VddStatus.NotInstalled or Display.VddStatus.NeedsSetup;
            _vddSetup.Text = st == Display.VddStatus.NeedsSetup ? "Activer l'écran virtuel…" : "Installer l'écran virtuel…";
            _vddSetup.Enabled = !_c.VirtualSetupRunning && !_c.Busy && _c.Active == null;
            var screens = _c.Screens();
            var multi = screens.Count > 1;
            _screenLabel.Visible = _screen.Visible = multi;
            if (multi)
            {
                var names = screens.Select(d => (string?)d.DeviceName).ToList();
                var labels = screens.Select(Display.DisplayLogic.Label).ToArray();
                if (!names.SequenceEqual(_screenNames) || _screen.Items.Count != labels.Length)
                {
                    _screen.Items.Clear();
                    _screen.Items.AddRange(labels);
                    _screenNames = names;
                }
                var current = Display.DisplayLogic.DuplicateTarget(screens, s.DuplicateScreen);
                _screen.SelectedIndex = Math.Max(0, _screenNames.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally { _loading = false; }
    }

    public event Action<bool>? ReplaceWinKChanged;

    private void Change(Action apply)
    {
        if (_loading) return;
        apply();
        _c.Settings.Save();
    }

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 14, 0, 4) };

    private void Refresh_()
    {
        if (IsDisposed) return;
        _status.Text = _c.State;
        _encoder.Text = _c.EncoderText;
        RefreshMode();

    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void ShowAndFront()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}
