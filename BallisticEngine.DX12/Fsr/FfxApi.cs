using System;
using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// P/Invoke bindings for the AMD FidelityFX unified host API (ffx_api), as exported by
// amd_fidelityfx_loader_dx12.dll. This is the loader shim that dispatches to the versioned provider DLLs
// (amd_fidelityfx_upscaler_dx12.dll for FSR upscaling). Headers: native/fsr/include/ffx_api/{ffx_api,
// ffx_api_types,ffx_api_dx12,ffx_upscale}.h — the structs below mirror those C structs byte-for-byte
// (x64 natural alignment). All structs are BLITTABLE (bools are bytes, pointers are IntPtr) so we can
// take addresses freely: the create-desc chain links via pNext pointers and MUST stay alive for the
// context's lifetime (Dx12FsrUpscaler pins it in unmanaged memory until ffxDestroyContext).
//
// Contract verified against the FidelityFX-SDK sample (FSR upscaler v4.1.0 = FSR4, falls back to FSR3.1).
internal static class FfxApi {
    const string Loader = "amd_fidelityfx_loader_dx12.dll";

    // ffxReturnCode_t: 0 == FFX_API_RETURN_OK.
    public const uint ReturnOk = 0;

    // Descriptor type IDs (FFX_API_MAKE_*_SUB_ID expanded).
    public const ulong DescTypeCreateUpscale        = 0x00010000; // effect UPSCALE, sub 0x00
    public const ulong DescTypeCreateBackendDx12    = 0x00000002; // backend DX12, sub 0x02
    public const ulong DescTypeCreateUpscaleVersion = 0x0001000b; // upscale, sub 0x0b
    public const ulong DescTypeDispatchUpscale      = 0x00010001; // upscale, sub 0x01
    public const ulong DescTypeQueryGetUpscaleRatio = 0x00010002; // upscale, sub 0x02
    public const ulong DescTypeQueryGetRenderRes    = 0x00010003; // upscale, sub 0x03

    // FfxApiCreateContextUpscaleFlags.
    public const uint UpscaleEnableHdr          = 1u << 0;
    public const uint UpscaleEnableDisplayResMv = 1u << 1;
    public const uint UpscaleEnableMvJitterCancel = 1u << 2;
    public const uint UpscaleEnableDepthInverted = 1u << 3;
    public const uint UpscaleEnableDepthInfinite = 1u << 4;
    public const uint UpscaleEnableAutoExposure = 1u << 5;
    public const uint UpscaleEnableDynamicRes   = 1u << 6;
    public const uint UpscaleEnableNonLinearColor = 1u << 8;

    // FfxApiUpscaleQualityMode.
    public const uint QualityNativeAA        = 0;
    public const uint QualityQuality         = 1;
    public const uint QualityBalanced        = 2;
    public const uint QualityPerformance     = 3;
    public const uint QualityUltraPerformance = 4;

    // FfxApiResourceState (the `state` field tells FSR each resource's CURRENT D3D12 state).
    public const uint StateCommon          = 1u << 0;
    public const uint StateUnorderedAccess = 1u << 1;
    public const uint StateComputeRead     = 1u << 2;
    public const uint StatePixelRead       = 1u << 3;
    public const uint StatePixelComputeRead = StatePixelRead | StateComputeRead;

    // FFX_UPSCALER_VERSION = MAKE_VERSION(4,1,0) = (4<<22)|(1<<12)|0.
    public const uint UpscalerVersion = (4u << 22) | (1u << 12) | 0u;

    [StructLayout(LayoutKind.Sequential)]
    public struct Header {
        public ulong Type;     // ffxStructType_t
        public IntPtr PNext;   // ffxApiHeader*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Dimensions2D { public uint Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FloatCoords2D { public float X, Y; }

    // FfxApiResourceDescription — 8 uints (unions resolved to the texture-named members).
    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceDescription {
        public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
    }

    // FfxApiResource { void* resource; FfxApiResourceDescription description; uint32_t state; }.
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
        public IntPtr Device;   // ID3D12Device*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DispatchDescUpscale {
        public Header Header;
        public IntPtr CommandList;          // ID3D12GraphicsCommandList*
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
        public byte EnableSharpening;       // C bool (1 byte)
        public float Sharpness;
        public float FrameTimeDelta;        // milliseconds
        public float PreExposure;           // > 0
        public byte Reset;                  // C bool (1 byte)
        public float CameraNear;
        public float CameraFar;
        public float CameraFovAngleVertical; // radians
        public float ViewSpaceToMetersFactor;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryDescGetRenderResolution {
        public Header Header;
        public uint DisplayWidth;
        public uint DisplayHeight;
        public uint QualityMode;
        public IntPtr POutRenderWidth;   // uint32_t*
        public IntPtr POutRenderHeight;  // uint32_t*
    }

    // ffxCreateContext(ffxContext* context, ffxCreateContextDescHeader* desc, const ffxAllocationCallbacks*)
    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxCreateContext(out IntPtr context, IntPtr desc, IntPtr memCb);

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxDestroyContext(ref IntPtr context, IntPtr memCb);

    // NOTE: configure/query/dispatch take ffxContext* (a POINTER to the handle), not the handle value.
    // ffxQuery for a GLOBAL (context-less) query is called with a null pointer (IntPtr.Zero) — valid.
    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxConfigure(ref IntPtr context, IntPtr desc);

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxQuery(IntPtr context, IntPtr desc);   // pass IntPtr.Zero for the global query

    [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ffxDispatch(ref IntPtr context, IntPtr desc);
}
