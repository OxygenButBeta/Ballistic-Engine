using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

internal static class XessApi {
    const string Lib = "libxess.dll";

    public const int ResultSuccess = 0;
    public const int ResultWarningOldDriver = 2;

    public const int QualityUltraPerformance = 100;
    public const int QualityPerformance      = 101;
    public const int QualityBalanced         = 102;
    public const int QualityQuality          = 103;
    public const int QualityUltraQuality     = 104;
    public const int QualityUltraQualityPlus = 105;
    public const int QualityAA               = 106;

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

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12InitParams {
        public Xess2D OutputResolution;
        public int QualitySetting;
        public uint InitFlags;
        public uint CreationNodeMask;
        public uint VisibleNodeMask;
        public IntPtr TempBufferHeap;
        public ulong BufferHeapOffset;
        public IntPtr TempTextureHeap;
        public ulong TextureHeapOffset;
        public IntPtr PipelineLibrary;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12ExecuteParams {
        public IntPtr ColorTexture;
        public IntPtr VelocityTexture;
        public IntPtr DepthTexture;
        public IntPtr ExposureScaleTexture;
        public IntPtr ResponsivePixelMaskTexture;
        public IntPtr OutputTexture;
        public float JitterOffsetX;
        public float JitterOffsetY;
        public float ExposureScale;
        public uint ResetHistory;
        public uint InputWidth;
        public uint InputHeight;
        public Xess2D InputColorBase;
        public Xess2D InputMotionVectorBase;
        public Xess2D InputDepthBase;
        public Xess2D InputResponsiveMaskBase;
        public Xess2D Reserved0;
        public Xess2D OutputColorBase;
        public IntPtr DescriptorHeap;
        public uint DescriptorHeapOffset;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessGetVersion(out Version pVersion);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xessD3D12CreateContext(IntPtr pDevice, out IntPtr phContext);

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
