using KairoCast.App.Display;

namespace KairoCast.Tests;

public class DisplayLogicTests
{
    private static DisplayInfo Real(int n, bool primary, int x, int y, int w = 1920, int h = 1080, bool attached = true) =>
        new($@"\\.\DISPLAY{n}", "Intel(R) Iris(R) Xe Graphics", @"PCI\VEN_8086&DEV_A7A1", @"MONITOR\BOE0B8E\{x}\0002", attached, primary, x, y, attached ? w : 0, attached ? h : 0);

    private static DisplayInfo Virtual(int n, bool attached, int x = 0, int w = 0, int h = 0) =>
        new($@"\\.\DISPLAY{n}", "Virtual Display Driver", @"Root\MttVDD", attached ? @"MONITOR\MTT1337\{x}\0003" : "", attached, false, x, 0, w, h);

    [Theory]
    [InlineData(@"Root\MttVDD", true)]
    [InlineData(@"ROOT\MTTVDD", true)]
    [InlineData("MttVDD", true)]
    [InlineData(@"PCI\VEN_8086&DEV_A7A1", false)]
    [InlineData(null, false)]
    public void HardwareId(string? id, bool expected) => Assert.Equal(expected, DisplayLogic.IsVddHardwareId(id));

    [Fact]
    public void RecognisesVirtualDisplay()
    {
        Assert.True(DisplayLogic.IsVirtual(Virtual(3, false)));
        Assert.True(DisplayLogic.IsVirtual(new DisplayInfo(@"\\.\DISPLAY5", "Autre nom", "", @"MONITOR\MTT1337\{x}", true, false)));
        Assert.True(DisplayLogic.IsVirtual(new DisplayInfo(@"\\.\DISPLAY5", "Virtual Display Driver", "", "", false, false)));
        Assert.False(DisplayLogic.IsVirtual(Real(1, true, 0, 0)));
    }

    [Fact]
    public void FindVirtualPrefersAttached()
    {
        var list = new[] { Real(1, true, 0, 0), Virtual(2, false), Virtual(3, true, 1920, 1920, 1080) };
        Assert.Equal(@"\\.\DISPLAY3", DisplayLogic.FindVirtual(list)!.DeviceName);
        Assert.Null(DisplayLogic.FindVirtual(new[] { Real(1, true, 0, 0) }));
    }

    [Fact]
    public void DuplicateCandidatesExcludeVirtualAndDetached()
    {
        var list = new[] { Real(2, false, 1920, 0), Virtual(3, true, 3840, 1920, 1080), Real(1, true, 0, 0), Real(4, false, 0, 0, attached: false) };
        var c = DisplayLogic.DuplicateCandidates(list);
        Assert.Equal(new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" }, c.Select(d => d.DeviceName));
    }

    [Fact]
    public void DuplicateTargetFallsBackToPrimary()
    {
        var list = new[] { Real(1, true, 0, 0), Real(2, false, 1920, 0) };
        Assert.Equal(@"\\.\DISPLAY2", DisplayLogic.DuplicateTarget(list, @"\\.\display2"));
        Assert.Equal(@"\\.\DISPLAY1", DisplayLogic.DuplicateTarget(list, @"\\.\DISPLAY9"));
        Assert.Equal(@"\\.\DISPLAY1", DisplayLogic.DuplicateTarget(list, null));
        Assert.Null(DisplayLogic.DuplicateTarget(Array.Empty<DisplayInfo>(), null));
        // L'écran virtuel n'est jamais recopié, même s'il a été choisi.
        Assert.Equal(@"\\.\DISPLAY1", DisplayLogic.DuplicateTarget(new[] { Real(1, true, 0, 0), Virtual(3, true, 1920, 1920, 1080) }, @"\\.\DISPLAY3"));
    }

    [Fact]
    public void ExtendPositionIsRightOfEverything()
    {
        Assert.Equal((1920, 0), DisplayLogic.ExtendPosition(new[] { Real(1, true, 0, 0) }));
        // Écran secondaire à gauche et plus haut : à droite du plus à droite, aligné sur le principal.
        Assert.Equal((2560, 0), DisplayLogic.ExtendPosition(new[] { Real(1, true, 0, 0, 2560, 1440), Real(2, false, -1920, -200) }));
        // L'écran virtuel déjà allumé ne compte pas.
        var v = Virtual(3, true, 1920, 1920, 1080);
        Assert.Equal((1920, 0), DisplayLogic.ExtendPosition(new[] { Real(1, true, 0, 0), v }, v.DeviceName));
        Assert.Equal((0, 0), DisplayLogic.ExtendPosition(Array.Empty<DisplayInfo>()));
    }

    [Fact]
    public void PickModeExactPrefers60Hz()
    {
        var modes = new[] { new DisplayMode(1920, 1080, 144), new DisplayMode(1920, 1080, 60), new DisplayMode(1920, 1080, 30), new DisplayMode(2560, 1440, 60) };
        Assert.Equal(new DisplayMode(1920, 1080, 60), DisplayLogic.PickMode(modes, 1920, 1080));
        Assert.Equal(new DisplayMode(2560, 1440, 60), DisplayLogic.PickMode(modes, 2560, 1440));
    }

    [Fact]
    public void PickModeWithout60Hz()
    {
        var modes = new[] { new DisplayMode(1920, 1080, 144), new DisplayMode(1920, 1080, 30) };
        Assert.Equal(new DisplayMode(1920, 1080, 30), DisplayLogic.PickMode(modes, 1920, 1080));
    }

    [Fact]
    public void PickModeFallsBackToSameAspectThatFits()
    {
        var modes = new[] { new DisplayMode(800, 600, 60), new DisplayMode(1366, 768, 60), new DisplayMode(1280, 720, 60), new DisplayMode(3840, 2160, 60) };
        Assert.Equal(new DisplayMode(1280, 720, 60), DisplayLogic.PickMode(modes, 1920, 1080));
        Assert.Equal(new DisplayMode(800, 600, 60), DisplayLogic.PickMode(new[] { new DisplayMode(800, 600, 60), new DisplayMode(3840, 2160, 60) }, 1920, 1080));
        Assert.Equal(new DisplayMode(3840, 2160, 60), DisplayLogic.PickMode(new[] { new DisplayMode(3840, 2160, 60) }, 1920, 1080));
        Assert.Null(DisplayLogic.PickMode(Array.Empty<DisplayMode>(), 1920, 1080));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY12", 12)]
    [InlineData("rien", 0)]
    [InlineData("", 0)]
    public void Number(string name, int n) => Assert.Equal(n, DisplayLogic.Number(name));

    [Fact]
    public void Labels()
    {
        Assert.Equal("Écran 1 (principal) – 1920×1080", DisplayLogic.Label(Real(1, true, 0, 0)));
        Assert.Equal("Écran 2 – 2560×1440", DisplayLogic.Label(Real(2, false, 1920, 0, 2560, 1440)));
    }

    [Fact]
    public void PackageDir()
    {
        var dirs = new[]
        {
            @"C:\Users\x\AppData\Local\Microsoft\WinGet\Packages\Autre.Paquet_Microsoft.Winget.Source_8wekyb3d8bbwe",
            @"C:\Users\x\AppData\Local\Microsoft\WinGet\Packages\VirtualDrivers.Virtual-Display-Driver_Microsoft.Winget.Source_8wekyb3d8bbwe",
        };
        Assert.Equal(dirs[1], DisplayLogic.PickPackageDir(dirs));
        Assert.Null(DisplayLogic.PickPackageDir(dirs.Take(1)));
        Assert.EndsWith(Path.Combine("x86", "VDD"), DisplayLogic.DriverSubdir(false));
        Assert.EndsWith(Path.Combine("ARM64", "VDD"), DisplayLogic.DriverSubdir(true));
    }

    [Fact]
    public void WingetCommands()
    {
        Assert.Contains("install --id VirtualDrivers.Virtual-Display-Driver -e", DisplayLogic.WingetInstallArgs);
        Assert.Contains("--accept-package-agreements", DisplayLogic.WingetInstallArgs);
        Assert.Equal("winget uninstall --id VirtualDrivers.Virtual-Display-Driver -e", DisplayLogic.WingetUninstallCommand);
    }

    [Fact]
    public void SettingsXmlHasOneMonitorAndBoxResolutions()
    {
        var doc = System.Xml.Linq.XDocument.Parse(DisplayLogic.SettingsXml());
        Assert.Equal("1", doc.Root!.Element("monitors")!.Element("count")!.Value);
        var res = doc.Root.Element("resolutions")!.Elements("resolution")
            .Select(r => $"{r.Element("width")!.Value}x{r.Element("height")!.Value}").ToList();
        Assert.Contains("1920x1080", res);
        Assert.Contains("2560x1440", res);
    }
}
