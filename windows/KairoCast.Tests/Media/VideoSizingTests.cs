using KairoCast.Media;

namespace KairoCast.Tests.Media;

public class VideoSizingTests
{
    [Fact]
    public void Fit_SameAspect_FillsWholeTarget()
    {
        Assert.Equal(new FitRect(0, 0, 1920, 1080), VideoSizing.Fit(2560, 1440, 1920, 1080));
    }

    [Fact]
    public void Fit_WiderSource_LetterboxesTopAndBottom()
    {
        // 21:9 (3440x1440) dans 1920x1080 : 1920 x 804 centré
        var r = VideoSizing.Fit(3440, 1440, 1920, 1080);
        Assert.Equal(1920, r.Width);
        Assert.Equal(804, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(138, r.Y);
    }

    [Fact]
    public void Fit_TallerSource_PillarboxesLeftAndRight()
    {
        // 16:10 (1920x1200) dans 1920x1080 : 1728 x 1080
        var r = VideoSizing.Fit(1920, 1200, 1920, 1080);
        Assert.Equal(1728, r.Width);
        Assert.Equal(1080, r.Height);
        Assert.Equal(96, r.X);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Fit_AlwaysEvenAndInside()
    {
        foreach (var (sw, sh) in new[] { (1366, 768), (1280, 1024), (1001, 999), (3, 1), (1, 3) })
        {
            var r = VideoSizing.Fit(sw, sh, 1920, 1080);
            Assert.True(r.Width % 2 == 0 && r.Height % 2 == 0 && r.X % 2 == 0 && r.Y % 2 == 0, $"{sw}x{sh} -> {r}");
            Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= 1920 && r.Y + r.Height <= 1080, $"{sw}x{sh} -> {r}");
            Assert.True(r.Width >= 2 && r.Height >= 2);
        }
    }

    [Fact]
    public void Fit_RejectsInvalidSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoSizing.Fit(0, 1080, 1920, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoSizing.Fit(1920, 1080, 1920, -2));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1921, 1081, 1920, 1080)]
    [InlineData(1, 1, 2, 2)]
    public void Even_RoundsDownToEven(int w, int h, int ew, int eh)
    {
        Assert.Equal((ew, eh), VideoSizing.Even(w, h));
    }
}
