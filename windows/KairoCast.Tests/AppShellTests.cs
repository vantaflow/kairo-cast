using KairoCast.App.Shell;
using KairoCast.Media;

namespace KairoCast.Tests;

public class SettingsTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "kc-" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.True(s.ReplaceWinK);
        Assert.True(s.AutoStart);
        Assert.True(s.SendAudio);
        Assert.False(s.FirewallAsked);
        Assert.Equal(QualityPreset.P1080F30, s.Quality);
        Assert.Empty(s.ManualBoxes);
    }

    [Fact]
    public void MissingFileGivesDefaults() => Assert.True(AppSettings.Load(Tmp()).ReplaceWinK);

    [Fact]
    public void RoundTrip()
    {
        var path = Tmp();
        var s = new AppSettings { ReplaceWinK = false, AutoStart = false, Quality = QualityPreset.P1440F30, SendAudio = false, FirewallAsked = true };
        s.ManualBoxes.Add("192.168.1.20:7400");
        Assert.True(s.Save(path));
        var r = AppSettings.Load(path);
        Assert.False(r.ReplaceWinK);
        Assert.False(r.AutoStart);
        Assert.False(r.SendAudio);
        Assert.True(r.FirewallAsked);
        Assert.Equal(QualityPreset.P1440F30, r.Quality);
        Assert.Equal(new[] { "192.168.1.20:7400" }, r.ManualBoxes);
        Assert.Contains("P1440F30", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{ pas du json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"Quality\":\"Inconnue\"}")]
    [InlineData("{\"Quality\":42}")]
    public void CorruptedGivesDefaults(string content)
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var s = AppSettings.Load(path);
        Assert.True(s.ReplaceWinK);
        Assert.Equal(QualityPreset.P1080F30, s.Quality);
    }

    [Theory]
    [InlineData(QualityPreset.P1080F30, 1920, 1080, 30)]
    [InlineData(QualityPreset.P1080F60, 1920, 1080, 60)]
    [InlineData(QualityPreset.P1440F30, 2560, 1440, 30)]
    public void QualityMapsToStreamOptions(QualityPreset q, int w, int h, int fps)
    {
        var o = new AppSettings { Quality = q, SendAudio = false }.ToStreamOptions();
        Assert.Equal((w, h, fps), (o.Width, o.Height, o.Fps));
        Assert.False(o.Audio);
        Assert.True(o.BitrateKbps >= 8000);
    }

    [Fact]
    public void DefaultQualityIs1080p30At8Mbps()
    {
        var o = new AppSettings().ToStreamOptions();
        Assert.Equal(8000, o.BitrateKbps);
        Assert.True(o.Audio);
    }
}

public class ManualAddressTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 7300)]
    [InlineData(" 10.0.0.5:7400 ", "10.0.0.5", 7400)]
    public void Valid(string text, string ip, int port)
    {
        Assert.True(ManualAddress.TryParse(text, out var a, out var p));
        Assert.Equal(ip, a.ToString());
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("::1")]
    [InlineData("1.2.3")]
    public void Invalid(string text) => Assert.False(ManualAddress.TryParse(text, out _, out _));
}

public class HotkeyFilterTests
{
    private const int K = HotkeyFilter.VkK, Win = HotkeyFilter.VkLWin;

    [Fact]
    public void WinKOpensPanelAndSwallowsKeyUp()
    {
        var f = new HotkeyFilter();
        Assert.Equal(HotkeyDecision.OpenPanel, f.Decide(K, false, true, false, true));
        Assert.Equal(HotkeyDecision.Swallow, f.Decide(K, false, true, false, true)); // répétition
        Assert.Equal(HotkeyDecision.Swallow, f.Decide(K, true, true, false, true));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(K, true, true, false, true)); // plus rien à avaler
    }

    [Fact]
    public void WinUpAfterSwallowedKIsReplayedWithDummy()
    {
        var f = new HotkeyFilter();
        f.Decide(K, false, true, false, true);
        f.Decide(K, true, true, false, true);
        Assert.Equal(HotkeyDecision.ReplayWinUp, f.Decide(Win, true, false, false, true));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(Win, true, false, false, true));
    }

    [Fact]
    public void PlainWinTapIsNotTouched()
    {
        var f = new HotkeyFilter();
        Assert.Equal(HotkeyDecision.Pass, f.Decide(Win, false, true, false, true));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(Win, true, false, false, true)); // Démarrer s'ouvre normalement
    }

    [Fact]
    public void KWithoutWinPasses() => Assert.Equal(HotkeyDecision.Pass, new HotkeyFilter().Decide(K, false, false, false, true));

    [Fact]
    public void OtherWinShortcutsPass()
    {
        var f = new HotkeyFilter();
        Assert.Equal(HotkeyDecision.Pass, f.Decide(0x44, false, true, false, true)); // Win+D
        Assert.Equal(HotkeyDecision.Pass, f.Decide(Win, true, false, false, true));
    }

    [Fact]
    public void PassThroughOnceLetsOneWinKThrough()
    {
        var f = new HotkeyFilter();
        f.ArmPassThrough();
        Assert.True(f.PassThroughArmed);
        Assert.Equal(HotkeyDecision.Pass, f.Decide(K, false, true, false, true));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(K, true, true, false, true));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(Win, true, false, false, true));
        Assert.False(f.PassThroughArmed);
        Assert.Equal(HotkeyDecision.OpenPanel, f.Decide(K, false, true, false, true)); // le suivant est de nouveau pris
    }

    [Fact]
    public void DisarmCancelsPassThrough()
    {
        var f = new HotkeyFilter();
        f.ArmPassThrough();
        f.DisarmPassThrough();
        Assert.Equal(HotkeyDecision.OpenPanel, f.Decide(K, false, true, false, true));
    }

    [Fact]
    public void ReplaceOffKeepsNativeWinK()
    {
        var f = new HotkeyFilter();
        Assert.Equal(HotkeyDecision.Pass, f.Decide(K, false, true, false, false));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(K, true, true, false, false));
        Assert.Equal(HotkeyDecision.Pass, f.Decide(Win, true, false, false, false));
    }

    [Fact]
    public void ReplaceOffWinAltKOpensPanel()
    {
        var f = new HotkeyFilter();
        Assert.Equal(HotkeyDecision.OpenPanel, f.Decide(K, false, true, true, false));
        Assert.Equal(HotkeyDecision.Swallow, f.Decide(K, true, true, true, false));
        Assert.Equal(HotkeyDecision.ReplayWinUp, f.Decide(HotkeyFilter.VkRWin, true, false, true, false));
    }

    [Fact]
    public void ReplaceOnWinAltKIsLeftToWindows() =>
        Assert.Equal(HotkeyDecision.Pass, new HotkeyFilter().Decide(K, false, true, true, true));
}
