using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// CommonSettings + ReblurSettings from NRD/Include/NRDSettings.h (v4.17), laid out byte-for-byte.
// CRITICAL: field order + types must match the C++ struct EXACTLY — a single wrong offset corrupts NRD silently.
// C++ bool = 1 byte (U1). float[N]/uint16_t[N] = fixed arrays. uint8_t enums = byte. Default packing.
internal static class NrdSettings {
    // nrd::CommonSettings — passed every frame via SetCommonSettings.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NrdCommonSettings {
        public fixed float ViewToClipMatrix[16];
        public fixed float ViewToClipMatrixPrev[16];
        public fixed float WorldToViewMatrix[16];
        public fixed float WorldToViewMatrixPrev[16];
        public fixed float WorldPrevToWorldMatrix[16];
        public fixed float MotionVectorScale[3];   // .z = 0 → 2D screen-space motion
        public fixed float CameraJitter[2];
        public fixed float CameraJitterPrev[2];
        public fixed ushort ResourceSize[2];
        public fixed ushort ResourceSizePrev[2];
        public fixed ushort RectSize[2];
        public fixed ushort RectSizePrev[2];
        public float ViewZScale;
        public float TimeDeltaBetweenFrames;
        public float DenoisingRange;
        public float DisocclusionThreshold;
        public float DisocclusionThresholdAlternate;
        public float CameraAttachedReflectionMaterialID;
        public float StrandMaterialID;
        public float HistoryFixAlternatePixelStrideMaterialID;
        public float StrandThickness;
        public float SplitScreen;
        public fixed ushort PrintfAt[2];
        public float Debug;
        public fixed uint RectOrigin[2];
        public uint FrameIndex;
        public NrdApi.AccumulationMode AccumulationMode;   // uint8_t
        [MarshalAs(UnmanagedType.U1)] public bool IsMotionVectorInWorldSpace;
        [MarshalAs(UnmanagedType.U1)] public bool IsHistoryConfidenceAvailable;
        [MarshalAs(UnmanagedType.U1)] public bool IsDisocclusionThresholdMixAvailable;
        [MarshalAs(UnmanagedType.U1)] public bool EnableValidation;

        // Construct with NRD's documented defaults (the C++ struct's in-class initializers).
        public static NrdCommonSettings Default() {
            var s = new NrdCommonSettings {
                ViewZScale = 1f,
                TimeDeltaBetweenFrames = 0f,
                DenoisingRange = 500000f,
                DisocclusionThreshold = 0.01f,
                DisocclusionThresholdAlternate = 0.05f,
                CameraAttachedReflectionMaterialID = 999f,
                StrandMaterialID = 999f,
                HistoryFixAlternatePixelStrideMaterialID = 999f,
                StrandThickness = 80e-6f,
                SplitScreen = 0f,
                Debug = 0f,
                FrameIndex = 0,
                AccumulationMode = NrdApi.AccumulationMode.CONTINUE,
                IsMotionVectorInWorldSpace = false,
                IsHistoryConfidenceAvailable = false,
                IsDisocclusionThresholdMixAvailable = false,
                EnableValidation = false,
            };
            // worldPrevToWorldMatrix defaults to identity; motionVectorScale defaults to (1,1,0); printfAt to 9999.
            s.WorldPrevToWorldMatrix[0] = 1f; s.WorldPrevToWorldMatrix[5] = 1f;
            s.WorldPrevToWorldMatrix[10] = 1f; s.WorldPrevToWorldMatrix[15] = 1f;
            s.MotionVectorScale[0] = 1f; s.MotionVectorScale[1] = 1f; s.MotionVectorScale[2] = 0f;
            s.PrintfAt[0] = 9999; s.PrintfAt[1] = 9999;
            return s;
        }
    }

    // nrd::ReblurHitDistanceParameters (passed inside ReblurSettings).
    [StructLayout(LayoutKind.Sequential)]
    public struct ReblurHitDistanceParameters { public float A, B, C; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ReblurAntilagSettings { public float LuminanceSigmaScale, LuminanceSensitivity; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ReblurResponsiveAccumulationSettings { public float RoughnessThreshold; public uint MinAccumulatedFrameNum; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ReblurConvergenceSettings { public float S, B, P; }   // s=overall scale, b=short-history, p=%affected

    // nrd::ReblurSettings — passed via SetDenoiserSettings. Defaults mirror the C++ in-class initializers.
    [StructLayout(LayoutKind.Sequential)]
    public struct ReblurSettings {
        public ReblurHitDistanceParameters HitDistanceParameters;
        public ReblurAntilagSettings AntilagSettings;
        public ReblurResponsiveAccumulationSettings ResponsiveAccumulationSettings;
        public ReblurConvergenceSettings ConvergenceSettings;
        public uint MaxAccumulatedFrameNum;
        public uint MaxFastAccumulatedFrameNum;
        public uint MaxStabilizedFrameNum;
        public uint HistoryFixFrameNum;
        public uint HistoryFixBasePixelStride;
        public uint HistoryFixAlternatePixelStride;
        public float FastHistoryClampingSigmaScale;
        public float DiffusePrepassBlurRadius;
        public float SpecularPrepassBlurRadius;
        public float MinHitDistanceWeight;
        public float MinBlurRadius;
        public float MaxBlurRadius;
        public float LobeAngleFraction;
        public float RoughnessFraction;
        public float PlaneDistanceSensitivity;
        public float FireflySuppressorMinRelativeScale;
        public float MinMaterialForDiffuse;
        public float MinMaterialForSpecular;
        public NrdApi.CheckerboardMode CheckerboardMode;                       // uint8_t
        public NrdApi.HitDistanceReconstructionMode HitDistanceReconstructionMode; // uint8_t
        [MarshalAs(UnmanagedType.U1)] public bool EnableAntiFirefly;
        [MarshalAs(UnmanagedType.U1)] public bool UsePrepassOnlyForSpecularMotionEstimation;
        [MarshalAs(UnmanagedType.U1)] public bool ReturnHistoryLengthInsteadOfOcclusion;

        public static ReblurSettings Default() => new() {
            HitDistanceParameters = new ReblurHitDistanceParameters { A = 3f, B = 0.1f, C = 20f },
            AntilagSettings = new ReblurAntilagSettings { LuminanceSigmaScale = 2f, LuminanceSensitivity = 3f },
            ResponsiveAccumulationSettings = new ReblurResponsiveAccumulationSettings { RoughnessThreshold = 0f, MinAccumulatedFrameNum = 3 },
            ConvergenceSettings = new ReblurConvergenceSettings { S = 1f, B = 0.2f, P = 0.8f },
            MaxAccumulatedFrameNum = 30,
            MaxFastAccumulatedFrameNum = 6,
            MaxStabilizedFrameNum = 63,   // REBLUR_MAX_HISTORY_FRAME_NUM
            HistoryFixFrameNum = 3,
            HistoryFixBasePixelStride = 14,
            HistoryFixAlternatePixelStride = 14,
            FastHistoryClampingSigmaScale = 2f,
            DiffusePrepassBlurRadius = 30f,
            SpecularPrepassBlurRadius = 50f,
            MinHitDistanceWeight = 0.1f,
            MinBlurRadius = 1f,
            MaxBlurRadius = 30f,
            LobeAngleFraction = 0.15f,
            RoughnessFraction = 0.15f,
            PlaneDistanceSensitivity = 0.02f,
            FireflySuppressorMinRelativeScale = 2f,
            MinMaterialForDiffuse = 4f,
            MinMaterialForSpecular = 4f,
            CheckerboardMode = NrdApi.CheckerboardMode.OFF,
            HitDistanceReconstructionMode = NrdApi.HitDistanceReconstructionMode.OFF,
            EnableAntiFirefly = true,
            UsePrepassOnlyForSpecularMotionEstimation = false,
            ReturnHistoryLengthInsteadOfOcclusion = false,
        };
    }
}
