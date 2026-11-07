using KairoCast.App.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace KairoCast.App.Capture;

/// <summary>
/// Périphérique D3D11 partagé par la capture, le processeur vidéo et l'encodeur matériel.
/// Protégé pour l'accès multithread (exigé par IMFDXGIDeviceManager et utilisé par plusieurs fils).
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public string AdapterName { get; }
    public long AdapterLuid { get; }
    public FeatureLevel FeatureLevel { get; }

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, FeatureLevel fl)
    {
        Device = device;
        Context = context;
        FeatureLevel = fl;
        using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
        {
            var d = adapter.Description;
            AdapterName = $"{d.Description} (vendor 0x{d.VendorId:X4}, device 0x{d.DeviceId:X4}, {(ulong)d.DedicatedVideoMemory / (1024 * 1024)} Mo)";
            AdapterLuid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
        }
        WinRtDevice = NativeInterop.CreateWinRtDevice(device);
    }

    public static GpuDevice Create()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out var device, out var fl, out var ctx);
        if (hr.Failure)
        {
            Log.Warn($"D3D11CreateDevice avec VIDEO_SUPPORT a échoué (hr=0x{hr.Code:X8}), nouvel essai sans");
            flags = DeviceCreationFlags.BgraSupport;
            hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out device, out fl, out ctx);
        }
        hr.CheckError();
        var gpu = new GpuDevice(device!, ctx!, fl);
        Log.Info($"GPU : {gpu.AdapterName}, LUID 0x{gpu.AdapterLuid:X}, niveau {fl}, options {flags}");
        return gpu;
    }

    public void Dispose()
    {
        try { (WinRtDevice as IDisposable)?.Dispose(); } catch { }
        Context.ClearState();
        Context.Dispose();
        Device.Dispose();
    }
}
