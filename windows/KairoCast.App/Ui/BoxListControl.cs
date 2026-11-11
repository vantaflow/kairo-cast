namespace KairoCast.App.Ui;

/// <summary>Liste des box trouvées, avec « Partager » / « Arrêter » ; partagée par le panneau et la fenêtre.</summary>
public sealed class BoxListControl : FlowLayoutPanel
{
    private readonly AppController _c;
    private readonly int _rowWidth;
    private readonly Color _fore;
    private bool _pending;

    public BoxListControl(AppController controller, int rowWidth, Color foreColor)
    {
        _c = controller;
        _rowWidth = LogicalToDeviceUnits(rowWidth);
        _fore = foreColor;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        _c.Changed += OnChanged;
        Rebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _c.Changed -= OnChanged;
        base.Dispose(disposing);
    }

    private void OnChanged()
    {
        if (!IsHandleCreated) { Rebuild(); return; }
        if (_pending) return;
        _pending = true;
        BeginInvoke(() => { _pending = false; Rebuild(); });
    }

    private void Rebuild()
    {
        if (IsDisposed) return;
        SuspendLayout();
        foreach (var old in Controls.Cast<Control>().ToList()) { Controls.Remove(old); old.Dispose(); }
        var visible = _c.Boxes.ToList();
        if (visible.Count == 0)
        {
            Controls.Add(new Label
            {
                Text = "Aucune box Kairo trouvée. Vérifiez que Kairo Cast est ouvert sur la box et que le PC est sur le même réseau.",
                ForeColor = _fore, AutoSize = false, Width = _rowWidth, Height = LogicalToDeviceUnits(52), Margin = new Padding(0, 4, 0, 4),
            });
        }
        foreach (var box in visible)
        {
            var active = _c.IsActive(box);
            var manual = _c.IsManual(box);
            var row = new TableLayoutPanel { Width = _rowWidth, Height = LogicalToDeviceUnits(36), ColumnCount = 3, Margin = new Padding(0, 2, 0, 2) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(104)));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, manual ? LogicalToDeviceUnits(34) : 0));
            row.Controls.Add(new Label
            {
                Text = BoxDisplay.Label(box, visible) + (manual ? "  (ajoutée à la main)" : "")
                       + (active ? "  ·  partagée depuis ce PC" : _c.StatusOf(box) is { } st ? "  ·  " + st.Describe() : ""), ForeColor = _fore, AutoEllipsis = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, active ? FontStyle.Bold : FontStyle.Regular),
            }, 0, 0);
            var b = new Button
            {
                Text = active ? "Arrêter" : "Partager", Dock = DockStyle.Fill, Enabled = !_c.Busy || active,
                FlatStyle = FlatStyle.System,
            };
            var captured = box;
            b.Click += async (_, _) =>
            {
                try { if (_c.IsActive(captured)) await _c.StopAsync(); else await _c.ShareAsync(captured); }
                catch (Exception e) { Diagnostics.Log.Error("Action sur la box", e); }
            };
            row.Controls.Add(b, 1, 0);
            if (manual)
            {
                var remove = new Button { Text = "✕", Dock = DockStyle.Fill, FlatStyle = FlatStyle.System, Enabled = !active };
                new ToolTip().SetToolTip(remove, "Retirer cette box de la liste");
                remove.Click += (_, _) => _c.RemoveManual(captured);
                row.Controls.Add(remove, 2, 0);
            }
            Controls.Add(row);
        }
        ResumeLayout(true);
    }
}
