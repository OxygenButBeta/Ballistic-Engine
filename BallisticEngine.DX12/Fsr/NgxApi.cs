using System;
using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// P/Invoke bindings for NVIDIA NGX (DLSS Super Resolution), via nvngx.dll. nvngx.dll is the NGX CORE loader
// that the NVIDIA GeForce/RTX DRIVER installs system-wide (it is NOT one of the shipped feature DLLs — we ship
// only nvngx_dlss.dll, the SR model snippet that the core loads). So on a non-NVIDIA box (or one without a
// recent driver) nvngx.dll is absent and the FIRST P/Invoke throws DllNotFoundException → the DLSS wrapper
// catches it and falls back. This is intentional: there is no way to run DLSS without the NVIDIA driver.
//
// We use NGX DIRECT (not Streamline). Rationale: Streamline's sl.interposer must HOOK D3D12 device/swapchain/
// queue creation (slSetD3DDevice + resource tagging via slSetTag + viewport/frame tokens + GUID-versioned
// SL_STRUCT chains) — that means rewriting Dx12Device's device creation, which is out of this change's lane,
// and the SL_STRUCT/GUID marshalling is painful from C#. NGX exposes plain C free-functions (the
// NVSDK_NGX_Parameter_SetX / NVSDK_NGX_D3D12_* exports below) that map 1:1 to DllImport, exactly like the FSR
// loader — the tractable path the task allows.
//
// The NVSDK_NGX_Parameter object is opaque (a C++ vtable interface); we never touch its layout — we only pass
// it as IntPtr and mutate it through the exported C accessor functions.
internal static class NgxApi {
    const string Lib = "nvngx.dll";

    // NVSDK_NGX_Result: SUCCESS = 0x1; the FAIL bit is 0xBAD00000. We treat (rc == Success) as ok.
    public const uint ResultSuccess = 0x1;

    // NVSDK_NGX_Version_API = 0x15 (NGX 1.5).
    public const uint VersionApi = 0x0000015;

    // NVSDK_NGX_Feature.
    public const uint FeatureSuperSampling = 1;

    // NVSDK_NGX_PerfQuality_Value (enum starts at 0 = MaxPerf).
    public const int PerfQualityMaxPerf          = 0;
    public const int PerfQualityBalanced         = 1;
    public const int PerfQualityMaxQuality       = 2;
    public const int PerfQualityUltraPerformance = 3;
    public const int PerfQualityUltraQuality     = 4;
    public const int PerfQualityDLAA             = 5;

    // NVSDK_NGX_DLSS_Feature_Flags.
    public const int DlssFlagNone          = 0;
    public const int DlssFlagIsHDR         = 1 << 0;
    public const int DlssFlagMVLowRes      = 1 << 1;
    public const int DlssFlagMVJittered    = 1 << 2;
    public const int DlssFlagDepthInverted = 1 << 3;
    public const int DlssFlagAutoExposure  = 1 << 6;

    // Parameter key strings (from nvsdk_ngx_defs.h). Passed as UTF-8 const char*.
    public const string P_Width            = "Width";
    public const string P_Height           = "Height";
    public const string P_OutWidth         = "OutWidth";
    public const string P_OutHeight        = "OutHeight";
    public const string P_PerfQualityValue = "PerfQualityValue";
    public const string P_CreationNodeMask = "CreationNodeMask";
    public const string P_VisibilityNodeMask = "VisibilityNodeMask";
    public const string P_FeatureCreateFlags = "DLSS.Feature.Create.Flags";
    public const string P_EnableOutputSubrects = "DLSS.Enable.Output.Subrects";
    public const string P_Color            = "Color";
    public const string P_Output           = "Output";
    public const string P_Depth            = "Depth";
    public const string P_MotionVectors    = "MotionVectors";
    public const string P_JitterX          = "Jitter.Offset.X";
    public const string P_JitterY          = "Jitter.Offset.Y";
    public const string P_Reset            = "Reset";
    public const string P_MVScaleX         = "MV.Scale.X";
    public const string P_MVScaleY         = "MV.Scale.Y";
    public const string P_Sharpness        = "Sharpness";
    public const string P_SuperSamplingAvailable = "SuperSampling.Available";
    public const string P_RenderSubrectW   = "DLSS.Render.Subrect.Dimensions.Width";
    public const string P_RenderSubrectH   = "DLSS.Render.Subrect.Dimensions.Height";

    // --- Lifecycle (NVSDK_NGX_D3D12_*). All take/return the NVSDK_NGX_Result uint. ---
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern uint NVSDK_NGX_D3D12_Init(ulong InApplicationId, string InApplicationDataPath,
        IntPtr InDevice, uint InSDKVersion);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_Shutdown();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_GetCapabilityParameters(out IntPtr OutParameters);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_AllocateParameters(out IntPtr OutParameters);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_DestroyParameters(IntPtr InParameters);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_CreateFeature(IntPtr InCmdList, uint InFeatureID,
        IntPtr InParameters, out IntPtr OutHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_ReleaseFeature(IntPtr InHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NVSDK_NGX_D3D12_EvaluateFeature(IntPtr InCmdList, IntPtr InFeatureHandle,
        IntPtr InParameters, IntPtr InCallback);

    // --- Parameter accessors (C free-function exports, ANSI const char* keys). ---
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern void NVSDK_NGX_Parameter_SetUI(IntPtr p, string name, uint value);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern void NVSDK_NGX_Parameter_SetI(IntPtr p, string name, int value);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern void NVSDK_NGX_Parameter_SetF(IntPtr p, string name, float value);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern void NVSDK_NGX_Parameter_SetD3d12Resource(IntPtr p, string name, IntPtr resource);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern uint NVSDK_NGX_Parameter_GetI(IntPtr p, string name, out int value);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern uint NVSDK_NGX_Parameter_GetUI(IntPtr p, string name, out uint value);
}
