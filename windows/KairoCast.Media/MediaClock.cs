using System.Diagnostics;

namespace KairoCast.Media;

/// <summary>Horloge commune audio/vidéo en microsecondes (Stopwatch, monotone), zéro au démarrage de la session.</summary>
public sealed class MediaClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public long NowUs => ToUs(Stopwatch.GetTimestamp() - _origin);

    /// <summary>Convertit un horodatage Stopwatch absolu en µs de cette horloge.</summary>
    public long FromTimestamp(long stopwatchTimestamp) => ToUs(stopwatchTimestamp - _origin);

    private static long ToUs(long ticks) => (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));

    /// <summary>µs → unités de 100 ns (Media Foundation).</summary>
    public static long ToHns(long us) => us * 10;

    public static long FromHns(long hns) => hns / 10;
}

/// <summary>Limite le débit d'images capturées à la cadence cible (la capture peut livrer 60 Hz ou plus).</summary>
public sealed class FramePacer
{
    private readonly long _intervalUs;
    private readonly long _toleranceUs;
    private long _lastUs = long.MinValue;

    public FramePacer(int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _intervalUs = 1_000_000L / fps;
        _toleranceUs = _intervalUs / 4;
    }

    public long IntervalUs => _intervalUs;

    public bool ShouldAccept(long nowUs)
    {
        if (_lastUs != long.MinValue && nowUs - _lastUs < _intervalUs - _toleranceUs) return false;
        _lastUs = nowUs;
        return true;
    }
}
