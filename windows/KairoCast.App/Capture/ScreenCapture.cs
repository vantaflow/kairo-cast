using System.Diagnostics;
using KairoCast.App.Diagnostics;
using KairoCast.Media;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace KairoCast.App.Capture;

/// <summary>
/// Capture de l'écran principal par Windows.Graphics.Capture, en textures D3D11 BGRA.
/// Le gestionnaire <see cref="FrameArrived"/> est appelé sur le fil du pool d'images : il doit rendre la main vite.
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int Buffers = 2;
    private const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";

    private readonly GpuDevice _gpu;
    private readonly MediaClock _clock;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private int _busy;
    private int _disposed;
    private long _frames;
    private long _errors;
    private bool _clockWarned;

    /// <summary>Texture BGRA (valide pendant l'appel seulement), largeur et hauteur utiles, date en µs (horloge commune).</summary>
    public event Action<ID3D11Texture2D, int, int, long>? FrameArrived;

    /// <summary>L'élément capturé a disparu (écran débranché…).</summary>
    public event Action? Closed;

    public int Width => _item.Size.Width;
    public int Height => _item.Size.Height;
    public long Frames => Interlocked.Read(ref _frames);

    public ScreenCapture(GpuDevice gpu, MediaClock clock)
    {
        _gpu = gpu;
        _clock = clock;
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows.Graphics.Capture n'est pas pris en charge sur ce PC");

        var hmon = NativeInterop.PrimaryMonitor();
        if (hmon == IntPtr.Zero) throw new InvalidOperationException("écran principal introuvable");
        _item = NativeInterop.CreateItemForMonitor(hmon);
        _item.Closed += (_, _) => { Log.Warn("Capture : l'écran capturé a été fermé"); Closed?.Invoke(); };
        _poolSize = _item.Size;
        Log.Info($"Capture : écran principal « {_item.DisplayName} » {_poolSize.Width}x{_poolSize.Height}");

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice, PixelFormat, Buffers, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);

        if (ApiInformation.IsPropertyPresent(SessionType, "IsCursorCaptureEnabled"))
        {
            try { _session.IsCursorCaptureEnabled = true; }
            catch (Exception e) { Log.Warn($"IsCursorCaptureEnabled : {Log.Describe(e)}"); }
        }
        else Log.Info("Capture : IsCursorCaptureEnabled absent (Windows trop ancien), curseur selon le système");

        if (ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired"))
        {
            var hr = NativeInterop.SetBorderRequired(_session, false);
            if (hr < 0) Log.Warn($"Capture : IsBorderRequired=false refusé ({Log.Hr(hr)}), la bordure jaune restera visible");
            else Log.Info("Capture : bordure jaune désactivée");
        }
        else Log.Info("Capture : IsBorderRequired absent (Windows 10), bordure jaune visible");
    }

    public void Start()
    {
        _session.StartCapture();
        Log.Info("Capture démarrée");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        // Les événements d'un pool libre ne se chevauchent pas en principe ; on se protège quand même.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;
            var pts = ToClock(frame.SystemRelativeTime);
            using (var surface = frame.Surface)
            using (var tex = NativeInterop.GetTexture(surface))
            {
                Interlocked.Increment(ref _frames);
                FrameArrived?.Invoke(tex, size.Width, size.Height, pts);
            }
            if (size.Width > 0 && size.Height > 0 && (size.Width != _poolSize.Width || size.Height != _poolSize.Height))
            {
                Log.Info($"Capture : taille d'écran changée {_poolSize.Width}x{_poolSize.Height} → {size.Width}x{size.Height}");
                _poolSize = size;
                sender.Recreate(_gpu.WinRtDevice, PixelFormat, Buffers, size);
            }
        }
        catch (Exception e)
        {
            var n = Interlocked.Increment(ref _errors);
            if (n <= 5 || n % 100 == 0) Log.Error($"Capture : erreur sur une image (n°{n})", e);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>SystemRelativeTime est en unités de 100 ns sur l'horloge QPC, comme Stopwatch.</summary>
    private long ToClock(TimeSpan systemRelativeTime)
    {
        var now = _clock.NowUs;
        var qpc = (long)(systemRelativeTime.Ticks * (Stopwatch.Frequency / 10_000_000.0));
        var us = _clock.FromTimestamp(qpc);
        if (us <= now && now - us < 1_000_000) return us;
        if (!_clockWarned)
        {
            _clockWarned = true;
            Log.Warn($"Capture : date système de l'image incohérente ({us} µs pour maintenant {now} µs), date de réception utilisée");
        }
        return now;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session.Dispose(); } catch (Exception e) { Log.Warn($"Capture : fermeture de session : {Log.Describe(e)}"); }
        try { _pool.Dispose(); } catch (Exception e) { Log.Warn($"Capture : fermeture du pool : {Log.Describe(e)}"); }
        Log.Info($"Capture arrêtée ({Frames} images reçues, {Interlocked.Read(ref _errors)} erreurs)");
    }
}
