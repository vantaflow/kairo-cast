using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace KairoCast.App.Capture;

/// <summary>
/// Ponts COM/WinRT écrits à la main (appels directs dans la vtable) : les aides de Vortice passent par
/// l'ancien marshaling COM, incompatible avec les projections CsWinRT de .NET 8.
/// </summary>
internal static unsafe class NativeInterop
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    // Vérifié dans les métadonnées du SDK Windows 10.0.22621 (Windows.Graphics.Capture.IGraphicsCaptureSession3).
    private static readonly Guid IidGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    private const uint MonitorDefaultToPrimary = 1;

    public static IntPtr PrimaryMonitor() => MonitorFromPoint(default, MonitorDefaultToPrimary);

    private static IntPtr* Vtbl(IntPtr p) => *(IntPtr**)p;

    /// <summary>IGraphicsCaptureItemInterop::CreateForMonitor (vtable : IUnknown 0-2, CreateForWindow 3, CreateForMonitor 4).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", IidGraphicsCaptureItemInterop);
        try
        {
            var thisPtr = factory.ThisPtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(thisPtr)[4];
            var iid = IidGraphicsCaptureItem;
            IntPtr item;
            var hr = fn(thisPtr, hmon, &iid, &item);
            Marshal.ThrowExceptionForHR(hr);
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally
        {
            factory.Dispose();
        }
    }

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var p));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface (slot 3) → ID3D11Texture2D (référence propre à l'appelant).</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var unk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iidAccess = IidDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iidAccess, out var access));
            try
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(access)[3];
                var iidTex = typeof(ID3D11Texture2D).GUID;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(fn(access, &iidTex, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>
    /// GraphicsCaptureSession.IsBorderRequired (Windows 11) par IGraphicsCaptureSession3 : la projection 19041 ne l'expose pas.
    /// Vtable : IInspectable 0-5, get_IsBorderRequired 6, put_IsBorderRequired 7. Renvoie le HRESULT.
    /// </summary>
    public static int SetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        var unk = ABI.Windows.Graphics.Capture.GraphicsCaptureSession.FromManaged(session);
        try
        {
            var iid = IidGraphicsCaptureSession3;
            var hr = Marshal.QueryInterface(unk, ref iid, out var s3);
            if (hr < 0) return hr;
            try
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Vtbl(s3)[7];
                return fn(s3, required ? (byte)1 : (byte)0);
            }
            finally { Marshal.Release(s3); }
        }
        finally { Marshal.Release(unk); }
    }
}

/// <summary>
/// ICodecAPI (strmif.h) : réglages de l'encodeur. Vtable : IUnknown 0-2, IsSupported 3, IsModifiable 4,
/// GetParameterRange 5, GetParameterValues 6, GetDefaultValue 7, GetValue 8, SetValue 9.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IidCodecApi = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");
    private IntPtr _p;

    private CodecApi(IntPtr p) => _p = p;

    public static CodecApi? From(IntPtr unknown, out int hr)
    {
        var iid = IidCodecApi;
        hr = Marshal.QueryInterface(unknown, ref iid, out var p);
        return hr >= 0 && p != IntPtr.Zero ? new CodecApi(p) : null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UI4;
        [FieldOffset(8)] public short Bool;
    }

    private const ushort VtBool = 11, VtUI4 = 19;

    private IntPtr* Vtbl => *(IntPtr**)_p;

    /// <summary>S_OK (0) si pris en charge, S_FALSE (1) ou E_NOTIMPL sinon.</summary>
    public int IsSupported(Guid key)
    {
        if (_p == IntPtr.Zero) return unchecked((int)0x80004003);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)Vtbl[3];
        return fn(_p, &key);
    }

    public int SetUInt32(Guid key, uint value)
    {
        var v = new Variant { Vt = VtUI4, UI4 = value };
        return Set(key, &v);
    }

    public int SetBool(Guid key, bool value)
    {
        var v = new Variant { Vt = VtBool, Bool = value ? (short)-1 : (short)0 };
        return Set(key, &v);
    }

    private int Set(Guid key, Variant* v)
    {
        if (_p == IntPtr.Zero) return unchecked((int)0x80004003);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)Vtbl[9];
        return fn(_p, &key, v);
    }

    public void Dispose()
    {
        var p = Interlocked.Exchange(ref _p, IntPtr.Zero);
        if (p != IntPtr.Zero) Marshal.Release(p);
    }
}

/// <summary>GUID des réglages de l'encodeur (codecapi.h) et attributs Media Foundation non exposés par Vortice.</summary>
internal static class CodecGuids
{
    public static readonly Guid AVLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    public static readonly Guid AVEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    public static readonly Guid AVEncMPVGOPSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

    /// <summary>MF_LOW_LATENCY (même GUID que CODECAPI_AVLowLatencyMode).</summary>
    public static readonly Guid MFLowLatency = AVLowLatencyMode;

    /// <summary>MFT_ENUM_ADAPTER_LUID : LUID de l'adaptateur d'un MFT matériel.</summary>
    public static readonly Guid MftEnumAdapterLuid = new("1D39518C-E220-4DA8-A07F-BA172552D6B1");

    public const uint RateControlCbr = 0;   // eAVEncCommonRateControlMode_CBR
    public const uint ProfileMain = 77;     // eAVEncH264VProfile_Main
    public const uint InterlaceProgressive = 2; // MFVideoInterlace_Progressive
    public const uint YuvMatrixBt709 = 1;       // MFVideoTransferMatrix_BT709
    public const uint NominalRange16To235 = 2;  // MFNominalRange_16_235
    public const uint PrimariesBt709 = 2;       // MFVideoPrimaries_BT709
    public const uint TransferFunc709 = 5;      // MFVideoTransFunc_709
}
