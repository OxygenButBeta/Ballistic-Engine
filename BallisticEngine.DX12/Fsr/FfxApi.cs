using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

internal static class FfxApi {
    const string Loader = "amd_fidelityfx_loader_dx12.dll";

    public const uint ReturnOk = 0;

    public const ulong DescTypeCreateUpscale        = 0x00010000;
    public const ulong DescTypeCreateBackendDx12    = 0x00000002;
    public const ulong DescTypeCreateUpscaleVersion = 0x0001000b;
    public const ulong DescTypeDispatchUpscale      = 0x00010001;
    public const ulong DescTypeQueryGetUpscaleRatio = 0x00010002;
    public const ulong DescTypeQueryGetRenderRes    = 0x00010003;

    public const uint UpscaleEnableHdr          = 1u << 0;
    public const uint UpscaleEnableDisplayResMv = 1u << 1;
    public const uint UpscaleEnableMvJitterCancel = 1u << 2;
    public const uint UpscaleEnableDepthInverted = 1u << 3;
    public const uint UpscaleEnableDepthInfinite = 1u << 4;
    public const uint UpscaleEnableAutoExposure = 1u << 5;
    public const uint UpscaleEnableDynamicRes   = 1u << 6;
    public const uint UpscaleEnableNonLinearColor = 1u << 8;

    public const uint QualityNativeAA        = 0;
    public const uint QualityQuality         = 1;
    public const uint QualityBalanced        = 2;
    public const uint QualityPerformance     = 3;
    public const uint QualityUltraPerformance = 4;

    public const uint StateCommon          = 1u << 0;
    public const uint StateUnorderedAccess = 1u << 1;
    public const uint StateComputeRead     = 1u << 2;
    public const uint StatePixelRead       = 1u << 3;
    public const uint StatePixelComputeRead = StatePixelRead | StateComputeRead;

    public const uint UpscalerVersion = (4u << 22) | (1u << 12) | 0u;

    [StructLayout(LayoutKind.Sequential)]
    public struct Header {
        public ulong Type;
        public IntPtr PNext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Dimensions2D { public uint Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FloatCoords2D { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceDescription {
        public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Resource {
        public IntPtr Res;
        public ResourceDescription Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateContextDescUpscale {
        public Header Header;
        public uint Flags;
        public Dimensions2D MaxRenderSize;
        public Dimensions2D MaxUpscaleSize;
        public IntPtr FpMessage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateBackendDX12Desc {
        public Header Header;
        public IntPtr Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DispatchDescUpscale {
        public Header Header;
        public IntPtr CommandList;
        public Resource Color;
        public Resource Depth;
        public Resource MotionVectors;
        public Resource Exposure;
        public Resource Reactive;
        public Resource TransparencyAndComposition;
        public Resource Output;
        public FloatCoords2D JitterOffset;
        public FloatCoords2D MotionVectorScale;
        public Dimensions2D RenderSize;
        public Dimensions2D UpscaleSize;
        public byte EnableSharpening;
        public float Sharpness;
        public float FrameTimeDelta;
        public float PreExposure;
        public byte Reset;
        public float CameraNear;
        public float CameraFar;
        public float CameraFovAngleVertical;
        public float ViewSpaceToMetersFactor;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryDescGetRenderResolution {
        public Header Header;
        public uint DisplayWidth;
        public uint DisplayHeight;
        public uint QualityMode;
        public IntPtr POutRenderWidth;
        public IntPtr POutRenderHeight;
    }

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxCreateContext(out IntPtr context, IntPtr desc, IntPtr memCb);

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxDestroyContext(ref IntPtr context, IntPtr memCb);

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxConfigure(ref IntPtr context, IntPtr desc);

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxQuery(IntPtr context, IntPtr desc);

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxDispatch(ref IntPtr context, IntPtr desc);
}
