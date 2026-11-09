using System.Runtime.InteropServices;
using KairoCast.App.Display;

namespace KairoCast.App.Ui;

/// <summary>Petit panneau sans bordure près de la zone de notification, ouvert par Win+K.</summary>
public sealed class CastPanel : Form
{
    private readonly Label _state = new();
    private readonly LinkLabel _others = new();
    private readonly Button _duplicate = new();
    private readonly Button _extend = new();
    private readonly FlowLayoutPanel _modes = new() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Visible = false };
    private readonly LinkLabel _vddHint = new() { AutoSize = false, Visible = false, LinkColor = Color.FromArgb(120, 170, 255) };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 80 };
    private DateTime _shownAt;
    private bool _wasActive;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>« Autres écrans (Windows)… » : à brancher sur l'envoi du Win+K natif.</summary>
    public event Action? OthersRequested;

    public CastPanel(AppController controller)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(32, 33, 38);
        ForeColor = Color.White;
        Padding = new Padding(14);
        KeyPreview = true;
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label { Text = "Partager l'écran vers…", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Color.White, Margin = new Padding(0, 0, 0, 6) };
        // Pendant un partage avec l'écran virtuel : raccourci de Win+P pour la TV seule (les autres écrans ne bougent pas).
        _modes.Margin = new Padding(0, 0, 0, 8);
        foreach (var (b, text, mode) in new[] { (_duplicate, "Dupliquer", TvLayout.Duplicate), (_extend, "Étendre", TvLayout.Extend) })
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 90);
            b.ForeColor = Color.White;
            b.AutoSize = false;
            b.Size = new Size(LogicalToDeviceUnits(156), LogicalToDeviceUnits(30));
            b.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0);
            b.Cursor = Cursors.Hand;
            b.Click += async (_, _) => await controller.SetTvLayoutAsync(mode);
            _modes.Controls.Add(b);
        }
        _vddHint.Text = "Pour choisir Dupliquer ou Étendre (Win+P) avec la TV : installer l'écran virtuel…";
        _vddHint.Size = new Size(LogicalToDeviceUnits(320), LogicalToDeviceUnits(36));
        _vddHint.Margin = new Padding(0, 0, 0, 6);
        _vddHint.LinkClicked += async (_, _) => { Hide(); await controller.SetupVirtualDisplayAsync(); };
        var list = new BoxListControl(controller, 320, Color.White);
        _state.AutoSize = false; _state.Width = LogicalToDeviceUnits(320); _state.Height = LogicalToDeviceUnits(22); _state.ForeColor = Color.FromArgb(160, 170, 190);
        _state.Margin = new Padding(0, 6, 0, 2);
        _others.Text = "Autres écrans (Windows)…";
        _others.AutoSize = true;
        _others.LinkColor = Color.FromArgb(120, 160, 255);
        _others.ActiveLinkColor = Color.White;
        _others.LinkClicked += (_, _) => { Hide(); OthersRequested?.Invoke(); };

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        flow.Controls.AddRange(new Control[] { title, _modes, _vddHint, list, _state, _others });
        Controls.Add(flow);

        controller.Changed += () => { _state.Text = controller.State; ShowMode(controller); if (Visible) BeginInvoke(Reposition); };
        _state.Text = controller.State;
        ShowMode(controller);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Activated += (_, _) => _wasActive = true;
        Deactivate += (_, _) => { if (Visible) Hide(); };
        // Filet : si le panneau n'a jamais pris le focus (premier plan refusé) ou si l'on clique ailleurs, on le ferme.
        _watch.Tick += (_, _) =>
        {
            if (!Visible) { _watch.Stop(); return; }
            var fg = GetForegroundWindow();
            var mine = fg == Handle;
            if (mine) _wasActive = true;
            var clickOutside = (((ushort)GetAsyncKeyState(0x01) | (ushort)GetAsyncKeyState(0x02) | (ushort)GetAsyncKeyState(0x04)) & 0x8000) != 0 && !Bounds.Contains(Cursor.Position);
            var lostFocus = (DateTime.UtcNow - _shownAt).TotalMilliseconds > 400 && !mine && (_wasActive || (DateTime.UtcNow - _shownAt).TotalSeconds > 1.5);
            if (clickOutside || lostFocus) Hide();
        };
    }

    private void ShowMode(AppController c)
    {
        var layout = c.TvLayout;
        var accent = Color.FromArgb(37, 99, 235);
        var idle = Color.FromArgb(48, 50, 58);
        _modes.Visible = layout != TvLayout.Off;
        _duplicate.BackColor = layout == TvLayout.Duplicate ? accent : idle;
        _extend.BackColor = layout is TvLayout.Extend or TvLayout.Only ? accent : idle;
        _vddHint.Visible = layout == TvLayout.Off && c.Settings.UseVirtualDisplay && !c.VirtualSetupRunning
                           && c.VirtualStatus is VddStatus.NotInstalled or VddStatus.NeedsSetup;
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
        _wasActive = false;
        _shownAt = DateTime.UtcNow;
        Show();
        Reposition(); // la taille définitive n'est connue qu'après l'affichage
        ForceForeground();
        Activate();
        _watch.Start();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _watch.Stop();
    }

    /// <summary>Un processus en arrière-plan n'a pas droit au premier plan : on se rattache au fil du premier plan le temps de le prendre.</summary>
    private void ForceForeground()
    {
        try
        {
            var fg = GetForegroundWindow();
            var fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { SetForegroundWindow(Handle); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
        }
        catch { }
    }

    /// <summary>Écran du curseur, collé au bord où se trouve la barre des tâches (déduit de l'écart entre WorkingArea et Bounds).</summary>
    private void Reposition()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        var b = screen.Bounds;
        var m = LogicalToDeviceUnits(12);
        int x = wa.Right - Width - m, y = wa.Bottom - Height - m; // barre en bas (ou aucune) : coin bas droit
        if (wa.Left > b.Left) x = wa.Left + m;                    // barre à gauche
        else if (wa.Right < b.Right) x = wa.Right - Width - m;    // barre à droite
        if (wa.Top > b.Top) y = wa.Top + m;                       // barre en haut
        Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
    }
}
