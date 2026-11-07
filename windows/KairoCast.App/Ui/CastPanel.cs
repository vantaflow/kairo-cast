namespace KairoCast.App.Ui;

/// <summary>Petit panneau sans bordure près de la zone de notification, ouvert par Win+K.</summary>
public sealed class CastPanel : Form
{
    private readonly Label _state = new();
    private readonly LinkLabel _others = new();

    /// <summary>« Autres écrans (Windows)… » : à brancher sur l'envoi du Win+K natif.</summary>
    public event Action? OthersRequested;

    public CastPanel(AppController controller)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(32, 33, 38);
        ForeColor = Color.White;
        Padding = new Padding(14);
        KeyPreview = true;
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label { Text = "Partager l'écran vers…", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Color.White, Margin = new Padding(0, 0, 0, 6) };
        var list = new BoxListControl(controller, 320, Color.White);
        _state.AutoSize = false; _state.Width = 320; _state.Height = 22; _state.ForeColor = Color.FromArgb(160, 170, 190);
        _state.Margin = new Padding(0, 6, 0, 2);
        _others.Text = "Autres écrans (Windows)…";
        _others.AutoSize = true;
        _others.LinkColor = Color.FromArgb(120, 160, 255);
        _others.ActiveLinkColor = Color.White;
        _others.LinkClicked += (_, _) => { Hide(); OthersRequested?.Invoke(); };

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        flow.Controls.AddRange(new Control[] { title, list, _state, _others });
        Controls.Add(flow);

        controller.Changed += () => { _state.Text = controller.State; if (Visible) BeginInvoke(Reposition); };
        _state.Text = controller.State;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => { if (Visible) Hide(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW : absent d'Alt+Tab
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => false;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(70, 75, 90));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        PerformLayout();
        Reposition();
        Show();
        Activate();
    }

    private void Reposition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
    }
}
