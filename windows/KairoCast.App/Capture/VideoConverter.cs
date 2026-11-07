using KairoCast.App.Diagnostics;
using KairoCast.Media;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;

namespace KairoCast.App.Capture;

/// <summary>
/// Conversion BGRA → NV12 et mise à l'échelle sur le GPU (processeur vidéo D3D11), vers un anneau de textures NV12
/// à la taille encodée. Bandes noires seulement si les proportions diffèrent. Appelé depuis le fil de capture.
/// </summary>
internal sealed class VideoConverter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _outW, _outH, _fps;
    private readonly ID3D11VideoDevice _vdev;
    private readonly ID3D11VideoContext _vctx;
    private readonly ID3D11VideoContext1? _vctx1;
    private ID3D11VideoProcessorEnumerator? _enum;
    private ID3D11VideoProcessor? _proc;
    private ID3D11VideoProcessorOutputView?[] _outViews;
    private int _inW, _inH;
    private ID3D11Texture2D? _copy;
    private bool _copyMode;
    private FitRect _lastFit;
    private int _lastContentW, _lastContentH;

    public ID3D11Texture2D[] Outputs { get; }

    public VideoConverter(GpuDevice gpu, int outW, int outH, int fps, int slots)
    {
        _gpu = gpu;
        _outW = outW;
        _outH = outH;
        _fps = fps;
        _vdev = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _vctx = gpu.Context.QueryInterface<ID3D11VideoContext>();
        try { _vctx1 = gpu.Context.QueryInterface<ID3D11VideoContext1>(); }
        catch (Exception e) { Log.Warn($"ID3D11VideoContext1 indisponible ({Log.Hr(e)}) : espace colorimétrique par défaut"); }

        Outputs = new ID3D11Texture2D[slots];
        _outViews = new ID3D11VideoProcessorOutputView?[slots];
        var desc = new Texture2DDescription
        {
            Width = (uint)outW,
            Height = (uint)outH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        for (var i = 0; i < slots; i++) Outputs[i] = gpu.Device.CreateTexture2D(desc);
        Log.Info($"Convertisseur : {slots} textures NV12 {outW}x{outH} (RenderTarget)");
    }

    /// <summary>Convertit <paramref name="src"/> (BGRA, contenu utile <paramref name="contentW"/>×<paramref name="contentH"/>) dans la texture <paramref name="slot"/>.</summary>
    public void Convert(ID3D11Texture2D src, int contentW, int contentH, int slot)
    {
        var sd = src.Description;
        var srcW = (int)sd.Width;
        var srcH = (int)sd.Height;
        EnsureProcessor(srcW, srcH, sd.Format);

        var input = src;
        if (_copyMode) input = CopyOf(src, sd);
        ID3D11VideoProcessorInputView iv;
        try
        {
            iv = CreateInputView(input);
        }
        catch (SharpGenException e) when (!_copyMode)
        {
            // Certains pilotes refusent une vue d'entrée sur la texture de capture : on passe par une copie.
            Log.Warn($"Vue d'entrée refusée sur la texture de capture ({Log.Hr(e)}, format {sd.Format}, bind {sd.BindFlags}, misc {sd.MiscFlags}) : passage en mode copie");
            _copyMode = true;
            input = CopyOf(src, sd);
            iv = CreateInputView(input);
        }

        using (iv)
        {
            var cw = Math.Clamp(contentW, 1, srcW);
            var ch = Math.Clamp(contentH, 1, srcH);
            var fit = VideoSizing.Fit(cw, ch, _outW, _outH);
            if (fit != _lastFit || cw != _lastContentW || ch != _lastContentH)
            {
                Log.Info($"Mise à l'échelle : source {cw}x{ch} (texture {srcW}x{srcH}) → {fit.Width}x{fit.Height} en ({fit.X},{fit.Y}) dans {_outW}x{_outH}");
                _lastFit = fit;
                _lastContentW = cw;
                _lastContentH = ch;
            }
            _vctx.VideoProcessorSetStreamSourceRect(_proc!, 0, true, new RawRect(0, 0, cw, ch));
            _vctx.VideoProcessorSetStreamDestRect(_proc!, 0, true, new RawRect(fit.X, fit.Y, fit.X + fit.Width, fit.Y + fit.Height));
            var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = iv } };
            _vctx.VideoProcessorBlt(_proc!, _outViews[slot]!, 0, 1, streams).CheckError();
        }
    }

    private ID3D11VideoProcessorInputView CreateInputView(ID3D11Texture2D tex) =>
        _vdev.CreateVideoProcessorInputView(tex, _enum!, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

    private ID3D11Texture2D CopyOf(ID3D11Texture2D src, Texture2DDescription sd)
    {
        if (_copy == null || _copy.Description.Width != sd.Width || _copy.Description.Height != sd.Height)
        {
            _copy?.Dispose();
            _copy = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = sd.Width,
                Height = sd.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sd.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
        }
        _gpu.Context.CopyResource(_copy, src);
        return _copy;
    }

    private void EnsureProcessor(int inW, int inH, Format inFormat)
    {
        if (_proc != null && inW == _inW && inH == _inH) return;
        DisposeProcessor();
        _inW = inW;
        _inH = inH;
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1),
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputFrameRate = new Rational((uint)_fps, 1),
            OutputWidth = (uint)_outW,
            OutputHeight = (uint)_outH,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enum = _vdev.CreateVideoProcessorEnumerator(content);
        try
        {
            var inSupport = _enum.CheckVideoProcessorFormat(inFormat);
            var outSupport = _enum.CheckVideoProcessorFormat(Format.NV12);
            Log.Info($"Processeur vidéo {inW}x{inH} {inFormat} → {_outW}x{_outH} NV12 : entrée {inSupport}, sortie NV12 {outSupport}");
            if ((outSupport & VideoProcessorFormatSupport.Output) == 0)
                Log.Error("Le processeur vidéo ne déclare pas NV12 en sortie : l'image risque d'être vide");
        }
        catch (Exception e) { Log.Warn($"CheckVideoProcessorFormat : {Log.Describe(e)}"); }

        _proc = _vdev.CreateVideoProcessor(_enum, 0);
        _vctx.VideoProcessorSetStreamFrameFormat(_proc, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_proc, 0, false);
        _vctx.VideoProcessorSetStreamOutputRate(_proc, 0, VideoProcessorOutputRate.Normal, false, null);
        _vctx.VideoProcessorSetOutputTargetRect(_proc, true, new RawRect(0, 0, _outW, _outH));
        _vctx.VideoProcessorSetOutputBackgroundColor(_proc, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        if (_vctx1 != null)
        {
            _vctx1.VideoProcessorSetStreamColorSpace1(_proc, 0, ColorSpaceType.RgbFullG22NoneP709);
            _vctx1.VideoProcessorSetOutputColorSpace1(_proc, ColorSpaceType.YcbcrStudioG22LeftP709);
        }
        else
        {
            // RGB plein, sortie YCbCr BT.709 plage 16-235.
            _vctx.VideoProcessorSetStreamColorSpace(_proc, 0, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 });
            _vctx.VideoProcessorSetOutputColorSpace(_proc, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 });
        }
        for (var i = 0; i < Outputs.Length; i++)
        {
            _outViews[i] = _vdev.CreateVideoProcessorOutputView(Outputs[i], _enum, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
        }
    }

    private void DisposeProcessor()
    {
        for (var i = 0; i < _outViews.Length; i++) { _outViews[i]?.Dispose(); _outViews[i] = null; }
        _proc?.Dispose();
        _proc = null;
        _enum?.Dispose();
        _enum = null;
    }

    public void Dispose()
    {
        DisposeProcessor();
        _copy?.Dispose();
        foreach (var t in Outputs) t?.Dispose();
        _vctx1?.Dispose();
        _vctx.Dispose();
        _vdev.Dispose();
    }
}
