using KairoCast.App.Shell;
using KairoCast.Media;

namespace KairoCast.App.Ui;

/// <summary>Fenêtre principale : box, adresse manuelle, qualité, son, raccourci, démarrage avec Windows, état.</summary>
public sealed class MainForm : Form
{
    private static readonly (string Label, QualityPreset Preset)[] Qualities =
    {
        ("1080p 30 i/s (par défaut)", QualityPreset.P1080F30),
        ("1080p 60 i/s", QualityPreset.P1080F60),
        ("1440p 30 i/s", QualityPreset.P1440F30),
    };

    private readonly AppController _c;
    private readonly Label _status = new() { AutoSize = false, Width = 400, Height = 40 };
    private readonly Label _encoder = new() { AutoSize = false, Width = 400, Height = 20, ForeColor = SystemColors.GrayText };
    private bool _loading;

    /// <summary>Faux tant que l'utilisateur ferme la fenêtre (elle se réduit alors dans la zone de notification).</summary>
    public bool AllowClose { get; set; }

    public MainForm(AppController controller, Icon? icon)
    {
        _c = controller;
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

        var ip = new TextBox { Width = 250, PlaceholderText = "192.168.1.20" };
        var add = new Button { Text = "Ajouter par adresse IP", AutoSize = true };
        var addError = new Label { AutoSize = false, Width = 400, Height = 20, ForeColor = Color.Firebrick };
        void DoAdd()
        {
            var err = _c.AddManual(ip.Text);
            addError.Text = err ?? "";
            if (err == null) ip.Clear();
        }
        add.Click += (_, _) => DoAdd();
        ip.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoAdd(); } };
        var addRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        addRow.Controls.AddRange(new Control[] { ip, add });
        flow.Controls.Add(addRow);
        flow.Controls.Add(addError);

        flow.Controls.Add(Heading("Qualité"));
        var quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
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

        _c.Changed += Refresh_;
        Refresh_();
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
