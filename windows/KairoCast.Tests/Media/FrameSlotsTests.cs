using KairoCast.Media;

namespace KairoCast.Tests.Media;

public class FrameSlotsTests
{
    [Fact]
    public void WriteThenTake_DeliversFrameOnce()
    {
        var s = new FrameSlots(3);
        var w = s.TryAcquireWrite();
        Assert.True(w >= 0);
        s.Publish(w, 1000);
        Assert.True(s.TryTakePending(out var slot, out var pts));
        Assert.Equal((w, 1000L), (slot, pts));
        Assert.False(s.TryTakePending(out _, out _));
    }

    [Fact]
    public void NewerFrameReplacesUntakenOne_AndCountsDrop()
    {
        var s = new FrameSlots(3);
        var a = s.TryAcquireWrite(); s.Publish(a, 1);
        var b = s.TryAcquireWrite(); s.Publish(b, 2);
        Assert.NotEqual(a, b);
        Assert.True(s.TryTakePending(out var slot, out var pts));
        Assert.Equal((b, 2L), (slot, pts));
        Assert.Equal(1, s.Replaced);
    }

    [Fact]
    public void SlotsHeldByEncoderOrLatestAreNeverOverwritten()
    {
        var s = new FrameSlots(3);
        var a = s.TryAcquireWrite(); s.Publish(a, 1);
        s.TryTakePending(out _, out _); // a détenu par l'encodeur, et c'est la dernière image
        var b = s.TryAcquireWrite(); s.Publish(b, 2);
        s.TryTakePending(out _, out _); // b détenu ; dernière = b
        var c = s.TryAcquireWrite();
        Assert.True(c >= 0 && c != a && c != b);
        s.CancelWrite(c);
        s.Release(a);
        var d = s.TryAcquireWrite();
        Assert.True(d == a || d == c);
        Assert.NotEqual(b, d);
    }

    [Fact]
    public void NoFreeSlot_ReturnsMinusOne()
    {
        var s = new FrameSlots(2);
        var a = s.TryAcquireWrite(); s.Publish(a, 1); s.TryTakePending(out _, out _);
        var b = s.TryAcquireWrite(); s.Publish(b, 2); s.TryTakePending(out _, out _);
        Assert.Equal(-1, s.TryAcquireWrite());
        s.Release(a);
        Assert.Equal(a, s.TryAcquireWrite());
    }

    [Fact]
    public void RepeatLast_HoldsLatestFrame()
    {
        var s = new FrameSlots(3);
        Assert.False(s.TryTakeLastForRepeat(out _));
        var a = s.TryAcquireWrite(); s.Publish(a, 1);
        s.TryTakePending(out _, out _);
        s.Release(a);
        Assert.True(s.TryTakeLastForRepeat(out var r));
        Assert.Equal(a, r);
        Assert.Equal(1, s.RefCount(a));
        s.Release(a);
        Assert.Equal(0, s.RefCount(a));
    }

    [Fact]
    public void ReleaseUnderflow_Throws()
    {
        var s = new FrameSlots(2);
        Assert.Throws<InvalidOperationException>(() => s.Release(0));
    }
}
