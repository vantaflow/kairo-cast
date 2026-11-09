using System.Net;
using KairoCast.Core;

namespace KairoCast;

/// <summary>Fenêtre unique : choix de la box et de l'écran, puis Recopier / Arrêter.</summary>
sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(11, 13, 26), Card = Color.FromArgb(22, 26, 46), Text1 = Color.FromArgb(238, 241, 251),
        Muted = Color.FromArgb(150, 158, 190), Accent = Color.FromArgb(76, 141, 255);

    readonly ListBox boxes = new() { IntegralHeight = false, BorderStyle = BorderStyle.None, Height = 120 };
    readonly Button search = new() { Text = "Rechercher" };
    readonly TextBox manual = new() { PlaceholderText = "ou adresse IP de la box, ex. 192.168.0.62" };
    readonly ComboBox screens = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox fluidity = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox sound = new() { Text = "Envoyer le son du PC", Checked = true, AutoSize = true };
    readonly Button go = new() { Text = "Recopier", Height = 44 };
    readonly Label status = new() { AutoSize = false, Height = 56 };
    Sender? sender;

    public MainForm()
    {
        Text = "Kairo Cast";
        ClientSize = new Size(440, 520);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        Controls.Add(layout);
        int w = ClientSize.Width - 40;

        Control Title(string t) => new Label { Text = t, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 12, 0, 6) };
        var head = new Label { Text = "Recopier l'écran sur la TV", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 4) };
        layout.Controls.Add(head);

        layout.Controls.Add(Title("Box"));
        foreach (var c in new Control[] { boxes, manual, screens, fluidity }) { c.Width = w; c.BackColor = Card; c.ForeColor = Text1; }
        layout.Controls.Add(boxes);
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0), WrapContents = false };
        manual.Width = w - 120;
        Style(search, false); search.Width = 112; search.Height = manual.Height + 2;
        row.Controls.Add(manual); row.Controls.Add(search);
        layout.Controls.Add(row);

        layout.Controls.Add(Title("Écran du PC"));
        layout.Controls.Add(screens);
        layout.Controls.Add(Title("Fluidité"));
        fluidity.Items.AddRange(new object[] { "60 images/s (recommandé)", "30 images/s (réseau faible)" });
        fluidity.SelectedIndex = 0;
        layout.Controls.Add(fluidity);
        sound.Margin = new Padding(0, 14, 0, 0);
        layout.Controls.Add(sound);

        Style(go, true); go.Width = w; go.Margin = new Padding(0, 18, 0, 8);
        layout.Controls.Add(go);
        status.Width = w; status.ForeColor = Muted;
        layout.Controls.Add(status);

        for (int i = 0; i < Screen.AllScreens.Length; i++)
        {
            var s = Screen.AllScreens[i];
            screens.Items.Add($"Écran {i + 1}{(s.Primary ? " (principal)" : "")} — {ScreenSize.Physical(s).Width}×{ScreenSize.Physical(s).Height}");
        }
        screens.SelectedIndex = Math.Max(0, Array.FindIndex(Screen.AllScreens, s => s.Primary));
        manual.Text = Prefs.LastAddress ?? "";

        search.Click += async (_, _) => await SearchAsync();
        go.Click += async (_, _) => await ToggleAsync();
        manual.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ToggleAsync(); } };
        boxes.SelectedIndexChanged += (_, _) => { if (boxes.SelectedItem != null) manual.Text = ""; };
        Shown += async (_, _) => await SearchAsync();
        FormClosing += (_, _) => sender?.Stop();
    }

    static void Style(Button b, bool primary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(50, 56, 84);
        b.BackColor = primary ? Accent : Card;
        b.ForeColor = Color.White;
        b.Cursor = Cursors.Hand;
        if (primary) b.Font = new Font("Segoe UI Semibold", 11f);
    }

    async Task SearchAsync()
    {
        search.Enabled = false;
        SetStatus("Recherche des box Kairo Cast…");
        try
        {
            var found = await Mdns.DiscoverAsync(TimeSpan.FromSeconds(2.5));
            boxes.Items.Clear();
            foreach (var r in found) boxes.Items.Add(r);
            var last = Prefs.LastAddress;
            int idx = found.FindIndex(r => r.Address.ToString() == last);
            if (found.Count > 0) { boxes.SelectedIndex = idx >= 0 ? idx : 0; manual.Text = ""; }
            SetStatus(found.Count switch
            {
                0 => "Aucune box trouvée. Vérifiez que Kairo Cast 1.1 est ouvert sur la box et que le PC est sur le même réseau, ou saisissez son adresse IP.",
                1 => "1 box trouvée.",
                _ => $"{found.Count} box trouvées.",
            });
        }
        catch (Exception e) { SetStatus("Recherche impossible : " + e.Message); }
        finally { search.Enabled = true; }
    }

    Receiver? Target()
    {
        var ip = manual.Text.Trim();
        if (ip.Length > 0)
            return IPAddress.TryParse(ip, out var a) ? new Receiver(ip, a, LinkProtocol.Port) : null;
        return boxes.SelectedItem as Receiver;
    }

    async Task ToggleAsync()
    {
        if (sender != null) { sender.Stop(); return; }
        var target = Target();
        if (target == null) { SetStatus("Choisissez une box ou saisissez une adresse IP valide."); return; }
        var s = new Sender();
        s.Status += m => Ui(() => { if (sender == s) SetStatus(m); });
        s.Ended += err => Ui(() =>
        {
            if (sender != s) return; // échec au démarrage : déjà traité
            sender = null;
            Running(false);
            SetStatus(err ?? "Recopie arrêtée.");
        });
        sender = s;
        Running(true); // « Arrêter » reste cliquable pendant le démarrage
        try
        {
            int idx = Math.Max(0, screens.SelectedIndex);
            await s.StartAsync(target, idx, new Rectangle(Point.Empty, ScreenSize.Physical(Screen.AllScreens[idx])), sound.Checked, fluidity.SelectedIndex == 0 ? 60 : 30);
            Prefs.LastAddress = target.Address.ToString();
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé (ou box fermée) pendant le démarrage : Ended a déjà mis l'interface à jour.
        }
        catch (Exception e)
        {
            if (sender == s)
            {
                sender = null;
                Running(false);
                SetStatus(e.Message);
            }
            s.Dispose();
        }
    }

    void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { } // fenêtre en cours de fermeture
    }

    void Running(bool on)
    {
        go.Text = on ? "Arrêter" : "Recopier";
        go.BackColor = on ? Color.FromArgb(214, 69, 80) : Accent;
        foreach (var c in new Control[] { boxes, manual, search, screens, fluidity, sound }) c.Enabled = !on;
    }

    void SetStatus(string m) => status.Text = m;
}

static class Prefs
{
    static readonly string File1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairoCast", "derniere-box.txt");

    public static string? LastAddress
    {
        get { try { return File.Exists(File1) ? File.ReadAllText(File1).Trim() : null; } catch { return null; } }
        set { try { Directory.CreateDirectory(Path.GetDirectoryName(File1)!); File.WriteAllText(File1, value ?? ""); } catch { } }
    }
}
