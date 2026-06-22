using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

internal static class NgxApi {
    const string Lib = "nvngx.dll";

    public const uint ResultSuccess = 0x1;

    public const uint VersionApi = 0x0000015;

    public const uint FeatureSuperSampling = 1;

    public const int PerfQualityMaxPerf          = 0;
    public const int PerfQualityBalanced         = 1;
    public const int PerfQualityMaxQuality       = 2;
    public const int PerfQualityUltraPerformance = 3;
    public const int PerfQualityUltraQuality     = 4;
    public const int PerfQualityDLAA             = 5;

    public const int DlssFlagNone          = 0;
    public const int DlssFlagIsHDR         = 1 << 0;
    public const int DlssFlagMVLowRes      = 1 << 1;
    public const int DlssFlagMVJittered    = 1 << 2;
    public const int DlssFlagDepthInverted = 1 << 3;
    public const int DlssFlagAutoExposure  = 1 << 6;

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
