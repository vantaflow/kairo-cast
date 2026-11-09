using KairoCast.App.Shell;
using KairoCast.Media;

namespace KairoCast.App.Ui;

/// <summary>Fenêtre principale : box, adresse manuelle, mode (Dupliquer/Étendre), qualité, son, raccourci, démarrage avec Windows, état.</summary>
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
    private readonly RadioButton _duplicate = new() { Text = "Dupliquer : la TV affiche le même écran que le PC", AutoSize = true };
    private readonly RadioButton _extend = new() { Text = "Étendre : la TV devient un deuxième écran", AutoSize = true };
    private readonly Label _screenLabel = new() { Text = "Écran à recopier :", AutoSize = true, Margin = new Padding(20, 6, 0, 0) };
    private readonly ComboBox _screen = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(20, 2, 0, 0) };
    private readonly Label _modeHint = new() { AutoSize = false, ForeColor = SystemColors.GrayText };
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

        flow.Controls.Add(Heading("Mode"));
        _screen.Width = LogicalToDeviceUnits(250);
        _modeHint.Size = new Size(LogicalToDeviceUnits(400), LogicalToDeviceUnits(36));
        flow.Controls.AddRange(new Control[] { _duplicate, _extend, _screenLabel, _screen, _modeHint });

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

        _duplicate.CheckedChanged += (_, _) => { if (!_loading && _duplicate.Checked) _c.SetShareMode(ShareMode.Duplicate); };
        _extend.CheckedChanged += (_, _) => { if (!_loading && _extend.Checked) _c.SetShareMode(ShareMode.Extend); };
        _screen.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _screen.SelectedIndex < 0 || _screen.SelectedIndex >= _screenNames.Count) return;
            _c.SetDuplicateScreen(_screenNames[_screen.SelectedIndex]);
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

        _c.Changed += Refresh_;
        Refresh_();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(RefreshMode);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        base.Dispose(disposing);
    }

    /// <summary>Mode choisi, et liste des écrans à recopier (affichée seulement s'il y en a plusieurs).</summary>
    private void RefreshMode()
    {
        if (IsDisposed) return;
        var s = _c.Settings;
        _loading = true;
        try
        {
            _duplicate.Checked = s.ShareMode == ShareMode.Duplicate;
            _extend.Checked = s.ShareMode == ShareMode.Extend;
            var screens = s.ShareMode == ShareMode.Duplicate ? _c.Screens() : Array.Empty<Display.DisplayInfo>();
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
            _modeHint.Text = s.ShareMode == ShareMode.Extend
                ? "Un écran virtuel est ajouté pendant le partage, à droite de l'écran principal. La première fois, Windows demande d'installer son pilote."
                : "";
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
