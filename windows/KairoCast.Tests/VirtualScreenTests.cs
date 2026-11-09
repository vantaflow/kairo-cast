using KairoCast.App.Display;

namespace KairoCast.Tests;

/// <summary>Double de l'affichage de Windows : une disposition (résumé) et un journal des appels, aucun appel système.</summary>
internal sealed class FakeDisplay : IDisplaySystem
{
    public readonly List<string> Calls = new();
    public string Layout = "PC+HDMI clone";
    public bool Connected;
    public VirtualInfo? Current;
    public bool RefuseClone, RefuseRestore, RefuseDatabase;
    /// <summary>Simule Windows qui réapplique une autre disposition au départ du moniteur virtuel.</summary>
    public string? LayoutAfterDisable;
    public List<WindowDesc> Wins = new();
    public readonly List<(long Handle, DisplayRect To)> Moved = new();
    public DisplayRect Work = new(0, 0, 1920, 1040);

    public DisplaySnapshot Save() { Calls.Add("save"); return new DisplaySnapshot("data:" + Layout, Layout); }

    public void Restore(DisplaySnapshot s)
    {
        Calls.Add("restore");
        if (RefuseRestore) throw new InvalidOperationException("refusé");
        Layout = s.Summary;
        Current = null;
    }

    public void ResetToDatabase()
    {
        Calls.Add("database");
        if (RefuseDatabase) throw new InvalidOperationException("refusé");
        Current = null;
    }

    public bool VirtualConnected() => Connected;

    public void PlaceVirtual(DisplaySnapshot? basis, TvLayout layout, string? cloneOf, int width, int height)
    {
        Calls.Add($"place {layout} {(basis == null ? "actuelle" : "sauvegarde")} {cloneOf ?? "principal"} {width}x{height}");
        if (layout == TvLayout.Duplicate && RefuseClone) throw new InvalidOperationException("recopie refusée");
        Current = layout == TvLayout.Duplicate
            ? new VirtualInfo(cloneOf ?? @"\\.\DISPLAY1", new DisplayRect(0, 0, 1920, 1080), layout)
            : new VirtualInfo(@"\\.\DISPLAY5", new DisplayRect(1920, 0, 1920, 1080), layout);
        Layout = (basis?.Summary ?? Layout) + " +TV " + layout;
    }

    public VirtualInfo? Virtual() => Current;
    public IReadOnlyList<WindowDesc> Windows() => Wins;
    public DisplayRect PrimaryWorkArea() => Work;
    public void MoveWindow(long handle, DisplayRect to) { Calls.Add($"move {handle}"); Moved.Add((handle, to)); }
}

internal sealed class FakeDevice : IVirtualDevice
{
    private readonly FakeDisplay _d;
    public VddStatus State = VddStatus.Ready;
    public bool Enabled;
    public bool NeverAppears, RefuseDisable;

    public FakeDevice(FakeDisplay d) => _d = d;

    public VddStatus Status() => State;
    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool on)
    {
        _d.Calls.Add(on ? "enable" : "disable");
        if (!on && RefuseDisable) throw new InvalidOperationException("tâche introuvable");
        Enabled = on;
        _d.Connected = on && !NeverAppears;
        if (!on)
        {
            _d.Current = null;
            if (_d.LayoutAfterDisable != null) _d.Layout = _d.LayoutAfterDisable;
        }
        else
        {
            // Ce qui s'est passé en vrai : à l'arrivée du moniteur, Windows passe tout en Étendre.
            _d.Layout = "Windows a tout étendu";
        }
    }
}

internal sealed class MemoryStore : IRestoreStore
{
    public DisplaySnapshot? Value;
    public readonly List<string> Calls;
    public MemoryStore(List<string> calls) => Calls = calls;
    public void Write(DisplaySnapshot s) { Calls.Add("marker"); Value = s; }
    public DisplaySnapshot? Read() => Value;
    public void Clear() { Calls.Add("clear"); Value = null; }
}

public class VirtualScreenTests
{
    private readonly FakeDisplay _d = new();
    private readonly FakeDevice _dev;
    private readonly MemoryStore _store;
    private readonly List<string> _log = new();
    private readonly VirtualScreen _vs;

    public VirtualScreenTests()
    {
        _dev = new FakeDevice(_d);
        _store = new MemoryStore(_d.Calls);
        _vs = Make();
    }

    private VirtualScreen Make() => new(_d, _dev, _store, _log.Add, sleep: _ => { }, timeout: TimeSpan.FromSeconds(1));

    [Fact]
    public void StartSavesBeforeTouchingAnythingThenClonesOverTheSavedLayout()
    {
        var name = _vs.Start(1920, 1080, null);

        Assert.Equal(@"\\.\DISPLAY1", name);
        Assert.Equal(new[] { "save", "marker", "enable", "place Duplicate sauvegarde principal 1920x1080" }, _d.Calls);
        Assert.Equal("PC+HDMI clone", _store.Value!.Summary);
        // Le clone HDMI est repris tel quel (pas la disposition « tout étendu » choisie par Windows à l'arrivée).
        Assert.Equal("PC+HDMI clone +TV Duplicate", _d.Layout);
        Assert.True(_vs.Active);
        Assert.Equal(TvLayout.Duplicate, _vs.Layout());
    }

    [Fact]
    public void StartClonesTheChosenScreen()
    {
        Assert.Equal(@"\\.\DISPLAY2", _vs.Start(1280, 720, @"\\.\DISPLAY2"));
        Assert.Contains(@"place Duplicate sauvegarde \\.\DISPLAY2 1280x720", _d.Calls);
    }

    [Theory]
    [InlineData(VddStatus.NotInstalled)]
    [InlineData(VddStatus.NeedsSetup)]
    public void StartWithoutReadyDriverTouchesNothing(VddStatus state)
    {
        _dev.State = state;
        Assert.Throws<InvalidOperationException>(() => _vs.Start(1920, 1080, null));
        Assert.Empty(_d.Calls);
        Assert.Null(_store.Value);
        Assert.False(_vs.Active);
    }

    [Fact]
    public void StopRestoresExactlyThenDisablesAndClearsTheMarker()
    {
        _vs.Start(1920, 1080, null);
        _d.Calls.Clear();

        _vs.Stop();

        Assert.Equal(new[] { "restore", "disable", "save", "clear" }, _d.Calls);
        Assert.Equal("PC+HDMI clone", _d.Layout);
        Assert.False(_dev.Enabled);
        Assert.Null(_store.Value);
        Assert.False(_vs.Active);
        Assert.Equal(TvLayout.Off, _vs.Layout());
    }

    [Fact]
    public void StopRestoresAgainIfWindowsChangesTheLayoutWhenTheMonitorLeaves()
    {
        _vs.Start(1920, 1080, null);
        _d.LayoutAfterDisable = "Windows a tout étendu";
        _d.Calls.Clear();

        _vs.Stop();

        Assert.Equal(new[] { "restore", "disable", "save", "restore", "clear" }, _d.Calls);
        Assert.Equal("PC+HDMI clone", _d.Layout);
    }

    [Fact]
    public void StopTwiceDoesNothingTheSecondTime()
    {
        _vs.Start(1920, 1080, null);
        _vs.Stop();
        _d.Calls.Clear();
        _vs.Stop();
        Assert.Empty(_d.Calls);
    }

    [Fact]
    public void FailedStartPutsEverythingBack()
    {
        _dev.NeverAppears = true;
        Assert.Throws<InvalidOperationException>(() => _vs.Start(1920, 1080, null));
        Assert.Equal(new[] { "save", "marker", "enable", "restore", "disable", "save", "clear" }, _d.Calls);
        Assert.False(_dev.Enabled);
        Assert.Equal("PC+HDMI clone", _d.Layout);
        Assert.False(_vs.Active);
        Assert.Null(_store.Value);
    }

    [Fact]
    public void RefusedCloneFallsBackToExtendKeepingTheOtherScreens()
    {
        _d.RefuseClone = true;
        Assert.Equal(@"\\.\DISPLAY5", _vs.Start(1920, 1080, null));
        Assert.Contains("place Extend sauvegarde principal 1920x1080", _d.Calls);
        Assert.Equal("PC+HDMI clone +TV Extend", _d.Layout);
    }

    [Fact]
    public void RestoreFallsBackToWindowsDatabase()
    {
        _vs.Start(1920, 1080, null);
        _d.RefuseRestore = true;
        _d.Calls.Clear();
        _vs.Stop();
        Assert.Equal("restore", _d.Calls[0]);
        Assert.Equal("database", _d.Calls[1]);
        Assert.Null(_store.Value);
    }

    [Fact]
    public void MarkerIsKeptWhenNothingCouldBeRestored()
    {
        _vs.Start(1920, 1080, null);
        _d.RefuseRestore = _d.RefuseDatabase = true;
        _vs.Stop();
        Assert.NotNull(_store.Value);
        Assert.False(_dev.Enabled);
        Assert.Contains(_log, l => l.Contains("Win+P"));
    }

    [Fact]
    public void DisableFailureStillRestoresTheLayout()
    {
        _vs.Start(1920, 1080, null);
        _dev.RefuseDisable = true;
        _vs.Stop();
        Assert.Contains("restore", _d.Calls);
        Assert.Null(_store.Value);
    }

    [Fact]
    public void RecoverAfterCrashRestoresFromTheMarker()
    {
        // Session précédente plantée : marqueur sur disque, pilote encore activé, Windows en « tout étendu ».
        _store.Value = new DisplaySnapshot("data", "PC+HDMI clone");
        _dev.Enabled = true;
        _d.Connected = true;
        _d.Layout = "Windows a tout étendu";
        _d.Current = new VirtualInfo(@"\\.\DISPLAY5", new DisplayRect(1920, 0, 1920, 1080), TvLayout.Extend);

        var fresh = Make();
        Assert.True(fresh.Recover());

        Assert.Equal("PC+HDMI clone", _d.Layout);
        Assert.False(_dev.Enabled);
        Assert.Null(_store.Value);
    }

    [Fact]
    public void RecoverWithoutMarkerDisablesALeftoverDriverOnly()
    {
        _dev.Enabled = true;
        Assert.True(_vs.Recover());
        Assert.Equal(new[] { "disable" }, _d.Calls);
    }

    [Fact]
    public void RecoverWithNothingToDoTouchesNothing()
    {
        Assert.False(_vs.Recover());
        Assert.Empty(_d.Calls);
    }

    [Fact]
    public void RecoverLeavesARunningSessionAlone()
    {
        _vs.Start(1920, 1080, null);
        _d.Calls.Clear();
        Assert.False(_vs.Recover());
        Assert.Empty(_d.Calls);
    }

    [Fact]
    public void StartAfterACrashRestoresFirst()
    {
        _store.Value = new DisplaySnapshot("old", "PC+HDMI clone");
        _d.Layout = "Windows a tout étendu";
        _vs.Start(1920, 1080, null);
        Assert.Equal("restore", _d.Calls[0]);
        Assert.Equal("PC+HDMI clone", _store.Value!.Summary);
    }

    [Fact]
    public void WindowsOnTheExtendedScreenComeBack()
    {
        _vs.Start(1920, 1080, null);
        _vs.SetLayout(TvLayout.Extend, null); // Win+P › Étendre, ou le raccourci du panneau
        Assert.Contains("place Extend actuelle principal 1920x1080", _d.Calls);
        var on = new DisplayRect(2000, 100, 800, 600);
        _d.Wins = new List<WindowDesc>
        {
            new(1, "Chrome_WidgetWin_1", true, false, false, on, "Edge"),
            new(2, "Notepad", true, false, false, new DisplayRect(100, 100, 800, 600), "Bloc-notes sur le PC"),
            new(3, "WorkerW", true, false, false, new DisplayRect(1920, 0, 1920, 1080)),
            new(4, "Progman", true, false, false, new DisplayRect(1920, 0, 1920, 1080)),
            new(5, "Tooltip", true, true, false, on),
            new(6, "Hidden", false, false, false, on),
        };

        _vs.Stop();

        Assert.Equal(new long[] { 1 }, _d.Moved.Select(m => m.Handle));
        Assert.Equal(new DisplayRect(80, 100, 800, 600), _d.Moved[0].To);
        // Fenêtres déplacées après la restauration.
        Assert.True(_d.Calls.IndexOf("move 1") > _d.Calls.IndexOf("restore"));
    }

    [Fact]
    public void NoWindowIsMovedWhenTheTvWasACopy()
    {
        _vs.Start(1920, 1080, null);
        _d.Wins = new List<WindowDesc> { new(1, "Notepad", true, false, false, new DisplayRect(100, 100, 800, 600)) };
        _vs.Stop();
        Assert.Empty(_d.Moved);
    }

    [Fact]
    public void CaptureDeviceFollowsWinP()
    {
        Assert.Null(_vs.CaptureDevice());
        _vs.Start(1920, 1080, null);
        Assert.Equal(@"\\.\DISPLAY1", _vs.CaptureDevice());
        _vs.SetLayout(TvLayout.Extend, null);
        Assert.Equal(@"\\.\DISPLAY5", _vs.CaptureDevice());
        _d.Current = null; // Win+P › Écran du PC uniquement
        Assert.Null(_vs.CaptureDevice());
    }

    [Fact]
    public void SetLayoutOutsideASessionIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => _vs.SetLayout(TvLayout.Extend, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => _vs.SetLayout(TvLayout.Only, null));
        Assert.Empty(_d.Calls);
    }

    [Fact]
    public void GuardRestoresAroundAnInstallation()
    {
        var r = _vs.Guard(() =>
        {
            _d.Layout = "Windows a tout étendu"; // le nouveau moniteur est arrivé pendant l'installation
            return 42;
        });
        Assert.Equal(42, r);
        Assert.Equal("PC+HDMI clone", _d.Layout);
        Assert.DoesNotContain("disable", _d.Calls);
        Assert.Null(_store.Value);
    }

    [Fact]
    public void GuardRestoresEvenWhenTheInstallationFails()
    {
        Assert.Throws<InvalidOperationException>(() => _vs.Guard<int>(() =>
        {
            _d.Layout = "Windows a tout étendu";
            throw new InvalidOperationException("échec");
        }));
        Assert.Equal("PC+HDMI clone", _d.Layout);
    }
}

public class FileRestoreStoreTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "kc-" + Guid.NewGuid().ToString("N"), "display-restore.json");

    [Fact]
    public void RoundTripAndClear()
    {
        var path = Tmp();
        var store = new FileRestoreStore(path);
        Assert.Null(store.Read());
        store.Write(new DisplaySnapshot("AAEC", "PC+HDMI clone"));
        // Relu par une autre instance (lancement suivant après un plantage).
        Assert.Equal(new DisplaySnapshot("AAEC", "PC+HDMI clone"), new FileRestoreStore(path).Read());
        Assert.False(File.Exists(path + ".tmp"));
        store.Clear();
        Assert.Null(store.Read());
        Assert.False(File.Exists(path));
        store.Clear();
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("{\"Data\":\"\",\"Summary\":\"x\"}")]
    public void CorruptMarkerIsIgnored(string content)
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Assert.Null(new FileRestoreStore(path).Read());
    }
}
