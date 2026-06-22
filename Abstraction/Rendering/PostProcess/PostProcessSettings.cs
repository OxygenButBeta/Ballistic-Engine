namespace BallisticEngine;

public sealed class PostProcessSettings {
    public float ExposureEV { get; set; } = 15f;
    public float ExposureCompensation { get; set; }
    public float Exposure { get; set; } = 1f;

    public ExposureMode ExposureMode { get; set; } = ExposureMode.Fixed;
    public MeteringMode MeteringMode { get; set; } = MeteringMode.CenterWeighted;

    public float AutoExposureLimitMin { get; set; } = 13f;
    public float AutoExposureLimitMax { get; set; } = 19f;
    public float AutoExposureSpeedDarkToLight { get; set; } = 3f;
    public float AutoExposureSpeedLightToDark { get; set; } = 2.5f;
    public float HistogramFilterMin { get; set; } = 40f;
    public float HistogramFilterMax { get; set; } = 95f;

    public float AdaptedExposureEV { get; set; } = 15f;

    public float ActiveExposureEV =>
        ExposureMode == ExposureMode.Fixed ? ExposureEV : AdaptedExposureEV;

    public float ExposureMultiplier =>
        Exposure / (1.2f * System.MathF.Pow(2f, ActiveExposureEV - ExposureCompensation));

    public bool BloomEnabled { get; set; } = true;
    public float BloomIntensity { get; set; } = 0.04f;

    public float BloomThreshold { get; set; } = 1f;

    public float BloomKnee { get; set; } = 0.5f;

    public bool SSAOEnabled { get; set; } = true;

    public float SSAORadius { get; set; } = 1.75f;
    public float SSAOIntensity { get; set; } = 1.0f;
    public float SSAOPower { get; set; } = 1.0f;
    public float SSAOThickness { get; set; } = 0.25f;
    public bool SSAOMultiBounce { get; set; } = true;
    public AoQuality SSAOQuality { get; set; } = AoQuality.Medium;
    public AoResolution SSAOResolution { get; set; } = AoResolution.Half;

    public bool TaaEnabled { get; set; } = true;
    public float TaaFeedback { get; set; } = 0.9f;

    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.Off;
    public float UpscaleSharpness { get; set; } = 0.5f;

    public bool SsrEnabled { get; set; } = false;
    public float SsrIntensity { get; set; } = 1f;
    public ReflectionMode ReflectionMode { get; set; } = ReflectionMode.Off;

    public bool VolumetricEnabled { get; set; }
    public float VolumetricIntensity { get; set; } = 1f;
    public float VolumetricDensity { get; set; } = 0.002f;
    public float VolumetricHeightFalloff { get; set; } = 0.04f;
    public float VolumetricBaseHeight { get; set; }
    public float VolumetricScattering { get; set; } = 1f;
    public float VolumetricAmbientScatter { get; set; } = 1f;
    public float VolumetricAnisotropy { get; set; } = 0.7f;
    public float VolumetricSunGlow { get; set; } = 0.3f;
    public float VolumetricSunGlowSharpness { get; set; } = 48f;
    public int VolumetricStepCount { get; set; } = 48;
    public float VolumetricMaxDistance { get; set; } = 120f;
    public float VolumetricFeedback { get; set; } = 0.9f;
    public Vector3 VolumetricTint { get; set; } = Vector3.One;

    public bool ShaftsEnabled { get; set; }
    public float ShaftIntensity { get; set; } = 1f;
    public float ShaftDensity { get; set; } = 0.05f;
    public float ShaftDecay { get; set; }
    public float ShaftSharpness { get; set; } = 0.85f;
    public Vector3 ShaftTint { get; set; } = Vector3.One;

    public bool DustEnabled { get; set; }
    public float DustIntensity { get; set; } = 0.5f;
    public float DustSize { get; set; } = 0.5f;
    public Vector3 DustDrift { get; set; } = new(0.15f, 0.08f, 0.05f);
    public float DustSparkle { get; set; } = 1f;

    public bool AerialPerspectiveEnabled { get; set; } = true;
    public float AerialPerspectiveIntensity { get; set; } = 1f;
    public float AerialPerspectiveStartDistance { get; set; } = 30f;
    public float AerialPerspectiveMaxDistance { get; set; } = 2000f;
    public float AerialPerspectiveDensityScale { get; set; } = 1f;
    public Vector3 AerialPerspectiveTint { get; set; } = Vector3.One;

    public int MsaaSamples { get; set; } = 4;

    public float ShadowMaxDistance { get; set; } = 60f;
    public int ShadowCascadeCount { get; set; } = 4;
    public float ShadowSplitDistribution { get; set; } = 0.7f;
    public float ShadowCascadeBlend { get; set; } = 0.15f;
    public int ShadowResolution { get; set; } = 2048;
    public int ShadowFiltering { get; set; } = 1;
    public float ShadowSoftness { get; set; } = 2f;

    public bool UseVirtualShadowMaps { get; set; }
    public int VsmResolution { get; set; } = 2048;
    public int VsmClipmapLevels { get; set; } = 12;
    public float VsmLevel0Extent { get; set; } = 4f;

    public bool ContactShadowsEnabled { get; set; }
    public float ContactShadowLength { get; set; } = 0.3f;
    public int ContactShadowSteps { get; set; } = 12;
    public float ContactShadowThickness { get; set; } = 0.5f;

    public bool RayTracedShadows { get; set; }

    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public float VignetteStrength { get; set; }
    public float VignetteRoundness { get; set; } = 1f;
    public Vector3 VignetteColor { get; set; } = Vector3.Zero;
    public float FilmGrain { get; set; }
    public float Sharpen { get; set; }

    public float ChromaticAberration { get; set; }
    public float LensDistortion { get; set; }

    public bool DofEnabled { get; set; }
    public float DofFocusDistance { get; set; } = 8f;
    public float DofFocalLength { get; set; } = 0.05f;
    public float DofAperture { get; set; } = 2.8f;
    public float DofMaxCoc { get; set; } = 0.03f;

    public bool MotionBlurEnabled { get; set; }
    public float MotionBlurIntensity { get; set; } = 1f;
    public int MotionBlurSamples { get; set; } = 12;
    public float MotionBlurMaxVelocity { get; set; } = 0.05f;

    // Aurora GI (HW-RT diffuse, per-triangle radiance cache + screen probes). Replaces the deleted DDGI dials.
    public bool AuroraEnabled { get; set; } = true;
    public float AuroraIntensity { get; set; } = 2f;          // master GI strength (tuned: visible indirect without washing out)
    public float AuroraSkyIntensity { get; set; } = 1.5f;     // skylight let in through open sky-visibility
    public int AuroraRayCount { get; set; } = 16;             // hemisphere rays per pixel (temporal accumulation cleans the rest)
    public int AuroraDenoisePasses { get; set; } = 1;         // à-trous spatial denoise iterations (0 = raw); adaptive bumps it on disocclusion
    public bool AuroraMultiBounce { get; set; } = true;       // accumulate multi-bounce in the radiance cache
    public int AuroraProbeOct { get; set; } = 6;              // octahedral tile resolution per probe (oct × oct cells)
    public int AuroraCardBudget { get; set; } = 50000;        // card-light records relit per frame (round-robin; 0 = unlimited)
    public bool AuroraReflections { get; set; } = true;       // feed RT reflections from the radiance cache
    public float AuroraAoStrength { get; set; } = 0f;         // GTAO darkening of the GI contact term (0 = none)
    public bool AuroraDebugRawIndirect { get; set; } = false; // debug: show raw indirect irradiance E
}
