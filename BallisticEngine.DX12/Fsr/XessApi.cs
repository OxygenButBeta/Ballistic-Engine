using System;
using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// P/Invoke bindings for Intel XeSS (libxess.dll). Mirrors the FfxApi pattern: thin DllImport over the native
// C entry points declared in native/xess/inc/xess/{xess,xess_d3d12}.h. All structs are BLITTABLE (the headers
// pack(8); on x64 these members are naturally 8-aligned so default Sequential layout matches). The context
// handle is an opaque pointer the app keeps for the context lifetime (created at init, destroyed in Dispose).
//
// libxess.dll runs an XMX path on Intel Arc and a DP4a fallback on any SM6.4 GPU (incl. AMD/NVIDIA) — so unlike
// DLSS it is NOT vendor-locked. xessD3D12CreateContext returns XESS_RESULT_ERROR_UNSUPPORTED_DEVICE on a
// pre-SM6.4 GPU, which the wrapper treats as "unavailable" → graceful fallback.
internal static class XessApi {
    const string Lib = "libxess.dll";

    // xess_result_t (only the codes the wrapper checks). SUCCESS = 0; warnings are >0; errors are <0.
    public const int ResultSuccess = 0;
    public const int ResultWarningOldDriver = 2;

    // xess_quality_settings_t.
    public const int QualityUltraPerformance = 100;
    public const int QualityPerformance      = 101;
    public const int QualityBalanced         = 102;
    public const int QualityQuality          = 103;
    public const int QualityUltraQuality     = 104;
    public const int QualityUltraQualityPlus = 105;
    public const int QualityAA               = 106;

    // xess_init_flags_t.
    public const uint InitFlagNone               = 0;
    public const uint InitFlagHighResMv          = 1 << 0;
    public const uint InitFlagInvertedDepth      = 1 << 1;
    public const uint InitFlagExposureScaleTex   = 1 << 2;
    public const uint InitFlagResponsiveMask     = 1 << 3;
    public const uint InitFlagUseNdcVelocity     = 1 << 4;
    public const uint InitFlagExternalDescHeap   = 1 << 5;
    public const uint InitFlagLdrInputColor      = 1 << 6;
    public const uint InitFlagJitteredMv         = 1 << 7;
    public const uint InitFlagEnableAutoexposure = 1 << 8;

    [StructLayout(LayoutKind.Sequential)]
    public struct Xess2D { public uint X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Version { public ushort Major, Minor, Patch, Reserved; }

    // xess_d3d12_init_params_t — the trailing optional pointers are null (XeSS allocates internally).
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12InitParams {
        public Xess2D OutputResolution;
        public int QualitySetting;        // xess_quality_settings_t (enum is 4 bytes)
        public uint InitFlags;
        public uint CreationNodeMask;
        public uint VisibleNodeMask;
        public IntPtr TempBufferHeap;     // ID3D12Heap* (null)
        public ulong BufferHeapOffset;
        public IntPtr TempTextureHeap;    // ID3D12Heap* (null)
        public ulong TextureHeapOffset;
        public IntPtr PipelineLibrary;    // ID3D12PipelineLibrary* (null)
    }

    // xess_d3d12_execute_params_t. Resources are raw ID3D12Resource* (NativePointer). Color/velocity/depth must
    // be in NON_PIXEL_SHADER_RESOURCE state; output in UNORDERED_ACCESS (header contract).
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12ExecuteParams {
        public IntPtr ColorTexture;
        public IntPtr VelocityTexture;
        public IntPtr DepthTexture;
        public IntPtr ExposureScaleTexture;
        public IntPtr ResponsivePixelMaskTexture;
        public IntPtr OutputTexture;
        public float JitterOffsetX;       // [-0.5, 0.5]
        public float JitterOffsetY;
        public float ExposureScale;       // default 1
        public uint ResetHistory;
        public uint InputWidth;
        public uint InputHeight;
        public Xess2D InputColorBase;
        public Xess2D InputMotionVectorBase;
        public Xess2D InputDepthBase;
        public Xess2D InputResponsiveMaskBase;
        public Xess2D Reserved0;
        public Xess2D OutputColorBase;
        public IntPtr DescriptorHeap;     // ID3D12DescriptorHeap* (null — XeSS owns its heap)
        public uint DescriptorHeapOffset;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessGetVersion(out Version pVersion);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessD3D12CreateContext(IntPtr pDevice, out IntPtr phContext);

    // pInitParams passed by ref (a const pointer in C).
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessD3D12Init(IntPtr hContext, in D3D12InitParams pInitParams);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessGetInputResolution(IntPtr hContext, in Xess2D pOutputResolution,
        int qualitySettings, out Xess2D pInputResolution);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessD3D12Execute(IntPtr hContext, IntPtr pCommandList, in D3D12ExecuteParams pExecParams);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessDestroyContext(IntPtr hContext);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessSetVelocityScale(IntPtr hContext, float x, float y);
}
