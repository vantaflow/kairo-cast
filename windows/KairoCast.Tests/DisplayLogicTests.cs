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

    // Laptop : carte Intel (adaptateur 1, sources 0 à 3) ; pilote virtuel (adaptateur 9, sources 0 et 1).
    // Disposition de l'utilisateur : écran du portable et TV HDMI en recopie (même source 1:0).
    private static readonly PathDesc[] Active =
    {
        new(0, "1:0", "1:100", true, false, @"\.\DISPLAY1", Primary: true),
        new(1, "1:0", "1:200", true, false, @"\.\DISPLAY1", Primary: true),
    };

    private static readonly PathDesc[] All =
    {
        Active[0], Active[1],
        new(2, "1:1", "1:200", false, false, @"\.\DISPLAY2"),
        new(3, "1:0", "9:300", false, true, @"\.\DISPLAY1"),
        new(4, "1:1", "9:300", false, true, @"\.\DISPLAY2"),
        new(5, "9:0", "9:300", false, true, @"\.\DISPLAY5"),
        new(6, "9:1", "9:300", false, true, @"\.\DISPLAY6"),
    };

    [Fact]
    public void CloneJoinsTheExistingHdmiClone()
    {
        var src = DisplayLogic.CloneSource(Active, null);
        Assert.Equal("1:0", src);
        Assert.Equal(3, DisplayLogic.ChooseVirtualPath(All, Active.Select(p => p.SourceKey), src));
    }

    [Fact]
    public void ExtendUsesAFreeSourceOfTheDriverAdapter()
    {
        Assert.Equal(5, DisplayLogic.ChooseVirtualPath(All, Active.Select(p => p.SourceKey), null));
        // Source 9:0 déjà prise : la suivante.
        Assert.Equal(6, DisplayLogic.ChooseVirtualPath(All, new[] { "1:0", "9:0" }, null));
        // Pas de source du pilote : une source libre de la carte graphique.
        Assert.Equal(4, DisplayLogic.ChooseVirtualPath(All.Take(5), new[] { "1:0" }, null));
        Assert.Null(DisplayLogic.ChooseVirtualPath(All.Take(4), new[] { "1:0" }, null));
    }

    [Fact]
    public void CloneOfUnknownSourceIsNull() => Assert.Null(DisplayLogic.ChooseVirtualPath(All, new[] { "1:0" }, "7:7"));

    [Fact]
    public void CloneSourcePrefersChosenScreen()
    {
        var two = new[] { new PathDesc(0, "1:0", "1:100", true, false, @"\.\DISPLAY1", true), new PathDesc(1, "1:1", "1:200", true, false, @"\.\DISPLAY2") };
        Assert.Equal("1:1", DisplayLogic.CloneSource(two, @"\.\display2"));
        Assert.Equal("1:0", DisplayLogic.CloneSource(two, @"\.\DISPLAY9"));
        Assert.Null(DisplayLogic.CloneSource(Array.Empty<PathDesc>(), null));
    }

    [Fact]
    public void LayoutFollowsWinP()
    {
        PathDesc V(string src) => new(9, src, "9:300", true, true);
        Assert.Equal(TvLayout.Off, DisplayLogic.LayoutOf(Active));
        Assert.Equal(TvLayout.Duplicate, DisplayLogic.LayoutOf(Active.Append(V("1:0"))));
        Assert.Equal(TvLayout.Extend, DisplayLogic.LayoutOf(Active.Append(V("9:0"))));
        Assert.Equal(TvLayout.Only, DisplayLogic.LayoutOf(new[] { V("9:0") }));
    }

    [Fact]
    public void AdaptersAreRemappedByDevicePathAfterAReboot()
    {
        var map = DisplayLogic.RemapAdapters(
            new[] { ("0:1111", @"\?\PCI#VEN_8086"), ("0:2222", @"\?\ROOT#DISPLAY#0000"), ("0:3333", "") },
            new[] { ("0:4444", @"\?\pci#ven_8086"), ("0:2222", @"\?\ROOT#DISPLAY#0000") });
        Assert.Equal(new Dictionary<string, string> { ["0:1111"] = "0:4444" }, map);
    }

    [Fact]
    public void WindowRescueSkipsShellToolsHiddenAndOtherScreens()
    {
        var tv = new DisplayRect(1920, 0, 1920, 1080);
        var on = new DisplayRect(2000, 50, 600, 400);
        var list = new[]
        {
            new WindowDesc(1, "Chrome_WidgetWin_1", true, false, false, on),
            new WindowDesc(2, "Progman", true, false, false, tv),
            new WindowDesc(3, "WorkerW", true, false, false, tv),
            new WindowDesc(4, "Shell_SecondaryTrayWnd", true, false, false, on),
            new WindowDesc(5, "Outil", true, true, false, on),
            new WindowDesc(6, "Invisible", false, false, false, on),
            new WindowDesc(7, "Masquée", true, false, true, on),
            new WindowDesc(8, "SurLePc", true, false, false, new DisplayRect(10, 10, 600, 400)),
            // À cheval : son centre décide.
            new WindowDesc(9, "ACheval", true, false, false, new DisplayRect(1800, 0, 400, 300)),
            new WindowDesc(10, "Vide", true, false, false, new DisplayRect(2000, 0, 0, 0)),
        };
        Assert.Equal(new long[] { 1, 9 }, DisplayLogic.WindowsToRescue(list, tv).Select(w => w.Handle));
    }

    [Fact]
    public void RelocateKeepsRelativePlaceAndShrinksIfNeeded()
    {
        var tv = new DisplayRect(1920, 0, 3840, 2160);
        var work = new DisplayRect(0, 0, 1920, 1040);
        Assert.Equal(new DisplayRect(100, 50, 800, 600), DisplayLogic.Relocate(new DisplayRect(2020, 50, 800, 600), tv, work));
        // Trop à droite : ramenée dans la zone de travail.
        Assert.Equal(new DisplayRect(1120, 440, 800, 600), DisplayLogic.Relocate(new DisplayRect(5000, 2000, 800, 600), tv, work));
        // Plus grande que l'écran principal (agrandie sur la TV 4K).
        Assert.Equal(new DisplayRect(0, 0, 1920, 1040), DisplayLogic.Relocate(tv, tv, work));
        // Position négative (écran à gauche) : bornée.
        Assert.Equal(new DisplayRect(0, 0, 800, 600), DisplayLogic.Relocate(new DisplayRect(1800, -50, 800, 600), tv, work));
    }

    [Fact]
    public void TaskRunsOnlyPnputilOnThisDevice()
    {
        var xml = DisplayLogic.TaskXml(true, @"ROOT\DISPLAY\0000", "S-1-5-21-1-2-3-1001", @"C:\Windows\System32\pnputil.exe");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var exec = doc.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal(@"C:\Windows\System32\pnputil.exe", exec.Element(ns + "Command")!.Value);
        Assert.Equal(@"/enable-device ""ROOT\DISPLAY\0000""", exec.Element(ns + "Arguments")!.Value);
        var principal = doc.Root.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.Equal("S-1-5-21-1-2-3-1001", principal.Element(ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(ns + "RunLevel")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        Assert.Null(doc.Root.Element(ns + "Triggers")); // jamais lancée d'elle-même
        Assert.Contains("/disable-device", DisplayLogic.TaskXml(false, @"ROOT\DISPLAY\0000", "S-1", "p"));
        Assert.NotEqual(DisplayLogic.TaskName(true), DisplayLogic.TaskName(false));
    }

    [Fact]
    public void TaskXmlEscapesValues()
    {
        var xml = DisplayLogic.TaskXml(false, @"ROOT\A&B<0>", "S-1", @"C:\p.exe");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Contains(@"ROOT\A&B<0>", doc.Root!.Value);
    }
}
