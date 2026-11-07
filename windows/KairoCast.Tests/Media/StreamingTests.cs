using KairoCast.Media;

namespace KairoCast.Tests.Media;

public class StreamingTests
{
    [Fact]
    public void Presets_MatchSpecDefaults()
    {
        Assert.Equal(new StreamOptions(), QualityPresets.Default);
        Assert.Equal(new StreamOptions(1920, 1080, 30, 8000), QualityPresets.Get(QualityPreset.P1080F30));
        var p60 = QualityPresets.Get(QualityPreset.P1080F60);
        Assert.Equal((1920, 1080, 60), (p60.Width, p60.Height, p60.Fps));
        Assert.True(p60.BitrateKbps > 8000);
        var p1440 = QualityPresets.Get(QualityPreset.P1440F30);
        Assert.Equal((2560, 1440, 30), (p1440.Width, p1440.Height, p1440.Fps));
        Assert.True(p1440.BitrateKbps > 8000);
    }

    [Theory]
    [InlineData(1920, 1080, 30, 8000)]
    [InlineData(1280, 720, 30, 3556)]
    [InlineData(1920, 1080, 60, 12000)]
    [InlineData(320, 180, 5, 1000)]
    public void SuggestKbps_ScalesWithPixelsAndFps(int w, int h, int fps, int expected)
    {
        Assert.Equal(expected, QualityPresets.SuggestKbps(w, h, fps));
    }

    [Fact]
    public void Validate_NormalisesOptions()
    {
        var o = new StreamOptions(1921, 1081, 0, 0).Validated();
        Assert.Equal((1920, 1080), (o.Width, o.Height));
        Assert.InRange(o.Fps, 1, 120);
        Assert.True(o.BitrateKbps > 0);
        Assert.Equal(120, new StreamOptions(Fps: 500).Validated().Fps);
        Assert.False(new StreamOptions(Audio: false).Validated().Audio);
    }

    [Fact]
    public void Pacer_AcceptsAtTargetRateFrom60HzSource()
    {
        var p = new FramePacer(30);
        var accepted = 0;
        for (var i = 0; i < 600; i++) // 10 s à 60 Hz
            if (p.ShouldAccept(i * 1_000_000L / 60)) accepted++;
        Assert.InRange(accepted, 295, 305);
    }

    [Fact]
    public void Pacer_AcceptsEveryFrameBelowTargetRate()
    {
        var p = new FramePacer(30);
        for (var i = 0; i < 50; i++) Assert.True(p.ShouldAccept(i * 50_000L)); // 20 Hz
    }

    [Fact]
    public void Clock_IsMonotonicMicroseconds()
    {
        var c = new MediaClock();
        var a = c.NowUs;
        Thread.Sleep(20);
        var b = c.NowUs;
        Assert.True(a >= 0);
        Assert.InRange(b - a, 15_000, 2_000_000);
        Assert.Equal(333_330, MediaClock.ToHns(33_333));
        Assert.Equal(33_333, MediaClock.FromHns(333_339));
    }
}
