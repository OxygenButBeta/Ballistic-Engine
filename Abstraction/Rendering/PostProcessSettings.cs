namespace BallisticEngine;

// How the camera EV is chosen each frame: dialed by hand, or metered from the rendered image.
public enum ExposureMode {
    Fixed,              // ExposureEV as authored
    Automatic,          // metered weighted average of scene luminance
    AutomaticHistogram, // metered, with percentile rejection of extreme dark/bright pixels
}

// Which pixels the automatic meter trusts (mirrors camera metering modes).
public enum MeteringMode {
    Average,        // every pixel counts equally
    CenterWeighted, // gaussian falloff from the screen center
    Spot,           // only a small center circle meters
}

// Temporal upscaling quality (FSR). Each mode is a fixed per-dimension render-resolution ratio; the
// upscaler reconstructs the display resolution. Higher ratio = lower internal res = faster, softer.
public enum UpscaleMode {
    Off,              // native-resolution render (no upscaler)
    NativeAA,         // 1.0x — FSR temporal AA at native res (replaces TAA, no resolution gain)
    Quality,          // 1.5x per dimension
    Balanced,         // 1.7x
    Performance,      // 2.0x
    UltraPerformance, // 3.0x
}

// Tunables for the HDR -> display pipeline. Neutral by default: only exposure,
// ACES tonemapping and gamma always run; everything stylistic is opt-in so the
// calibrated PBR output isn't silently distorted.
public sealed class PostProcessSettings {
    // PHYSICAL exposure. The scene is lit in real relative-luminance units (sun in lux-scale,
    // IBL as measured environment light), so brightness is controlled like a camera: by EV100.
    // Higher EV = darker image (more light needed to expose), matching photographic convention.
    // ExposureEV is the scene's middle-grey EV; ExposureCompensation nudges it in stops.
    // The renderer converts these to the multiplier 1/(1.2 * 2^(EV - comp)) fed to the tonemap.
    // `Exposure` remains as a raw manual multiplier (1 = use the EV path untouched).
    public float ExposureEV { get; set; } = 15f;          // matches an ~80000-lux physical sun
    public float ExposureCompensation { get; set; }       // stops; +1 = one stop brighter
    public float Exposure { get; set; } = 1f;             // legacy manual multiplier on top of EV

    // AUTOMATIC exposure (eye adaptation). The renderer meters last frame's HDR buffer,
    // recovers absolute scene luminance (the buffer is pre-exposed, so the meter divides the
    // frame's multiplier back out - no feedback loop), and eases AdaptedExposureEV toward the
    // metered EV. Fixed mode ignores all of this and uses ExposureEV directly.
    public ExposureMode ExposureMode { get; set; } = ExposureMode.Fixed;
    public MeteringMode MeteringMode { get; set; } = MeteringMode.CenterWeighted;
    public float AutoExposureLimitMin { get; set; } = 8f;           // EV floor the meter may reach
    public float AutoExposureLimitMax { get; set; } = 17f;          // EV ceiling the meter may reach
    public float AutoExposureSpeedDarkToLight { get; set; } = 3f;   // stops/sec when the scene brightens
    public float AutoExposureSpeedLightToDark { get; set; } = 2.5f; // stops/sec when the scene darkens (a day->night cut spans ~10 stops; 1.0 took 10+ s to settle)
    public float HistogramFilterMin { get; set; } = 40f;            // percentile below which pixels are rejected
    public float HistogramFilterMax { get; set; } = 95f;            // percentile above which pixels are rejected

    // Runtime slot written by the renderer's auto-exposure pass each frame; not user data.
    public float AdaptedExposureEV { get; set; } = 15f;

    // The EV the pipeline actually exposes with this frame.
    public float ActiveExposureEV =>
        ExposureMode == ExposureMode.Fixed ? ExposureEV : AdaptedExposureEV;

    // Physical exposure multiplier from the EV dials: standard ISO 100 / K=12.5 photometry.
    public float ExposureMultiplier =>
        Exposure / (1.2f * System.MathF.Pow(2f, ActiveExposureEV - ExposureCompensation));

    public bool BloomEnabled { get; set; } = true;
    public float BloomIntensity { get; set; } = 0.04f;
    // HDR threshold with a soft knee; values below it leak progressively less into bloom.
    public float BloomThreshold { get; set; } = 1f;

    public bool SSAOEnabled { get; set; } = true;
    // 0.5 was tuned for tabletop props; at that radius architectural crevices (window
    // recesses, cornices, arches) get no contact darkening and large surfaces read flat.
    public float SSAORadius { get; set; } = 1.75f; // world units
    public float SSAOIntensity { get; set; } = 1.3f;

    // Temporal anti-aliasing: jittered rendering + history accumulation. Replaces MSAA
    // (MSAA is forced off while TAA runs) and also smooths specular/SSAO/SSR noise.
    public bool TaaEnabled { get; set; } = true;
    public float TaaFeedback { get; set; } = 0.9f; // history weight; higher = smoother, more ghosting

    // Temporal upscaling (AMD FidelityFX FSR, DX12 only). Renders the scene at a lower internal
    // resolution and reconstructs the display resolution from jittered frames + motion vectors. When
    // active it REPLACES TAA (FSR does its own temporal AA). Off = native-res render (current behavior).
    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.Off;
    public float UpscaleSharpness { get; set; } = 0.5f;   // RCAS sharpening, 0 = none .. 1 = max

    // Screen-space reflections: smooth surfaces reflect the actual scene instead of only
    // the sky cubemap. Requires the normal attachment (unavailable in the MSAA path).
    public bool SsrEnabled { get; set; } = true;
    public float SsrIntensity { get; set; } = 1f;

    // Screen-space global illumination: a coarse one-bounce diffuse gather that adds
    // indirect fill light from sunlit on-screen surfaces into shadowed areas (the
    // directional bounce a flat ambient term can't provide). Like SSR it needs the normal
    // attachment, so it only runs while TAA is on / MSAA is off.
    public bool SsgiEnabled { get; set; } = true;

    // -- Quality / noise --
    // Rays per pixel: with temporal accumulation + the denoiser, even 2-4 stays clean.
    public int SsgiRayCount { get; set; } = 4;
    public float SsgiMaxHistory { get; set; } = 24f;  // temporal frames to accumulate (smoother/laggier)
    public float SsgiDenoise { get; set; } = 2f;      // spatial denoiser tap spacing (wider = smoother)

    // -- Ray shape --
    public float SsgiRayLength { get; set; } = 12f;   // metres; near vs far bounce reach
    public float SsgiFalloff { get; set; } = 0.5f;    // distance falloff exponent (0 = none); gentle so far walls still bounce
    public float SsgiThickness { get; set; } = 0.5f;  // depth-test tolerance during the march

    // -- THE dial: one cinematic look slider (0 = off-ish/neutral, ~0.6 = filmic default,
    // 1 = strong hero grade). It internally drives warmth, shadow lift, colour-bleed punch,
    // filmic contrast and stability inside the SSGI combine, so a good look needs nothing more
    // than this. The physical/artistic knobs below remain as advanced overrides. --
    public float SsgiLook { get; set; } = 0.6f;

    // Debug: show ONLY the bounce SSGI would add (brightened 10x so faint GI reads), instead of
    // scene+bounce. Black = no gather at that pixel. The fastest way to see whether/where SSGI
    // is actually contributing, and to tune ray length/intensity against real output.
    public bool SsgiDebugView { get; set; }

    // Sky contribution for rays that miss every on-screen surface (0..1). DEFAULT 0: the
    // forward shader's IBL irradiance already integrates the FULL sky, so adding sky again on
    // every missed ray double-counts it — in open scenes most rays miss, and SSGI degenerated
    // into a flat gray veil over the whole frame (washed contrast, milky shadows). The old
    // non-zero default predates the IBL-as-ambient-base refactor, when a zero miss made GI
    // collapse wherever the bright source left the screen; the IBL base killed that failure
    // mode, leaving only the double-count. The dial remains for windowless interiors where a
    // directional sky gather through openings can be worth it.
    public float SsgiSkyFallback { get; set; }

    // -- Bounce strength (advanced). SSGI is now a REFINEMENT on the physical IBL base, so it
    // only adds the local one-bounce colour - intensity ~1 (not the old 1.5 that compensated
    // for a missing ambient base). AmbientFloor/BounceBoost are retired: the IBL is the floor. --
    public float SsgiIntensity { get; set; } = 1f;                    // local-bounce strength
    public OpenTK.Mathematics.Vector3 SsgiTint { get; set; } = OpenTK.Mathematics.Vector3.One; // bounce colour
    public float SsgiSaturation { get; set; } = 1f;                   // bounce colour punch
    public float SsgiOcclusionPower { get; set; } = 0.6f;             // how hard AO bites the bounce
    public float SsgiMultiBounce { get; set; } = 0.5f;                // re-bounce fraction (fake multi-bounce)
    public float SsgiBounceBoost { get; set; }                        // retired (kept 0; IBL carries richness)
    public float SsgiAmbientFloor { get; set; }                       // retired (kept 0; physical IBL is the base)

    // Volumetric height fog + sun scattering (god-rays): physical exponential height fog
    // marched against the directional shadow map. In-scatters the atmosphere-attenuated sun
    // and the baked sky's average radiance (skylight); its transmittance EXTINGUISHES the
    // scene behind it (real fog hides things). Half-res march + temporal denoise; like
    // SSR/SSGI it reconstructs from the single-sample depth, so it only runs while TAA is
    // on / MSAA is off. Off by default (it's an atmospheric, scene-dependent look).
    public bool VolumetricEnabled { get; set; }
    public float VolumetricIntensity { get; set; } = 1f;              // master strength: fades whole fog below 1, boosts only glow above
    public float VolumetricDensity { get; set; } = 0.002f;            // extinction sigma_t at base height (1/m); 0.002 ~= 2km visibility (light haze)
    public float VolumetricHeightFalloff { get; set; } = 0.04f;       // 1/m: fog thins with altitude (0 = uniform medium)
    public float VolumetricBaseHeight { get; set; }                   // world Y below which the fog is at full density
    public float VolumetricScattering { get; set; } = 1f;             // sun in-scatter multiplier (1 = physical balance)
    public float VolumetricAmbientScatter { get; set; } = 1f;         // skylight in-scatter multiplier (1 = physical balance)
    public float VolumetricAnisotropy { get; set; } = 0.7f;           // forward HG lobe g; higher = tighter/brighter toward the sun
    public float VolumetricSunGlow { get; set; } = 0.3f;              // extra blaze around the sun disk seen through fog
    public float VolumetricSunGlowSharpness { get; set; } = 48f;      // how tight the sun-disk glow is (higher = smaller/hotter)
    public int VolumetricStepCount { get; set; } = 48;                // shadowed raymarch samples (cost vs banding)
    public float VolumetricMaxDistance { get; set; } = 120f;          // metres of shadowed march; fog continues analytically beyond
    public float VolumetricFeedback { get; set; } = 0.9f;             // temporal history weight (smoother/laggier)
    public OpenTK.Mathematics.Vector3 VolumetricTint { get; set; } = OpenTK.Mathematics.Vector3.One; // in-scatter colour grade

    // 1 = off. Offscreen targets are recreated when this changes. Ignored while TAA is on.
    public int MsaaSamples { get; set; } = 4;

    // Cascaded sun shadows (driven by the Shadows volume component; per-light acne bias stays
    // on DirectionalLight). Resolution is snapped to a power of two by the renderer and the
    // shadow array is recreated when it changes.
    public float ShadowMaxDistance { get; set; } = 60f;
    public int ShadowCascadeCount { get; set; } = 4;     // 1..4 (shader MAX_CASCADES)
    public float ShadowSplitDistribution { get; set; } = 0.7f; // 0 = uniform, 1 = logarithmic
    public float ShadowCascadeBlend { get; set; } = 0.15f;     // cross-fade width per cascade
    public int ShadowResolution { get; set; } = 2048;
    public int ShadowFiltering { get; set; } = 1;        // 0 = hard, 1 = PCF, 2 = PCSS
    public float ShadowSoftness { get; set; } = 2f;      // PCSS penumbra scale (1 = physical)

    // Contact (screen-space) shadows: a short depth-buffer ray march toward the sun that catches
    // the fine object-to-ground occlusion the cascades miss at their texel size. Off by default.
    public bool ContactShadowsEnabled { get; set; }
    public float ContactShadowLength { get; set; } = 0.3f;     // world metres marched
    public int ContactShadowSteps { get; set; } = 12;
    public float ContactShadowThickness { get; set; } = 0.5f;  // depth window counted as a hit

    // Stylistic extras, all neutral/off by default.
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public float VignetteStrength { get; set; }
    public float VignetteRoundness { get; set; } = 1f;  // 1 = circular, 0 = aspect-following oval
    public OpenTK.Mathematics.Vector3 VignetteColor { get; set; } = OpenTK.Mathematics.Vector3.Zero; // darken toward black by default
    public float FilmGrain { get; set; }
    public float Sharpen { get; set; }

    // Lens artefacts (a perfect edge-to-edge sharp, perfectly-aligned-channel frame is the
    // strongest "CG, not photographed" tell). Both neutral/off by default.
    public float ChromaticAberration { get; set; }  // lateral RGB split toward the frame edge
    public float LensDistortion { get; set; }        // barrel(+)/pincushion(-) warp

    // Depth of field (thin-lens bokeh). The single biggest "everything in focus = CG" fix.
    // Off by default; a scene opts in via the DepthOfField volume component. Physical controls:
    // focus distance + f-number + focal length, so it behaves like a real camera lens.
    public bool DofEnabled { get; set; }
    public float DofFocusDistance { get; set; } = 8f;   // metres to the focal plane
    public float DofFocalLength { get; set; } = 0.05f;  // 50mm-ish; larger = shallower DoF
    public float DofAperture { get; set; } = 2.8f;      // f-number; smaller = shallower DoF
    public float DofMaxCoc { get; set; } = 0.03f;       // blur-radius clamp (fraction of frame height)

    // ---- Realtime GI stack (GlobalIllumination volume override) -----------------------------------
    // The auto-fit probes + reflections + SDF-GI all run by DEFAULT (no setup); these dials let a
    // GlobalIllumination volume tune their strength without disabling the default behaviour. All
    // default to "current behaviour" so a no-volume scene is unchanged:
    //   * GiProbeIntensity / GiReflectionIntensity scale the baked diffuse / specular ambient.
    //   * GiSdfIntensityScale multiplies the SDF-GI off-screen bounce (on top of its own default,
    //     and on top of the probe<->SDF blend the renderer already applies).
    //   * GiSdfForceEnabled lets a volume turn the SDF-GI bounce ON without the env var (it stays
    //     env-gated by default while it matures; a scene that wants it just adds the volume).
    public float GiProbeIntensity { get; set; } = 1f;       // baked diffuse-probe ambient strength
    public float GiReflectionIntensity { get; set; } = 1f;  // baked local-reflection strength (× the volume's own Intensity)
    public float GiSdfIntensityScale { get; set; } = 1f;    // extra multiplier on the SDF-GI bounce
    public bool GiSdfForceEnabled { get; set; }             // turn SDF-GI on without BALLISTIC_SDFGI
    // Tiny ambient shadow-fill (fraction of albedo, AO-modulated) so enclosed interiors never crush
    // the shadowed side of geometry to PURE BLACK (UE interiors always have bounce fill). Small by
    // default so lit areas are ~unchanged; raise for flatter/brighter ambient, 0 for physically-pure.
    public float GiAmbientFloor { get; set; } = 0.03f;

    // Auto-fit probe-grid density multipliers (the IrradianceVolume/ReflectionVolume are automatic;
    // these scale how finely they're sampled). 1 = default auto-fit resolution.
    public float GiProbeDensity { get; set; } = 1f;
    public float GiReflectionDensity { get; set; } = 1f;

    // Debug gizmo toggles (also on the Scene-view toolbar): draw the probe / reflection grids.
    public bool GiDebugShowProbes { get; set; }
    public bool GiDebugShowReflectionProbes { get; set; }

    // Per-system master switches (the split LightProbes / ReflectionProbes / Lumen volume overrides).
    // Default ON for diffuse + specular probes (they run by default); Lumen default OFF (env/override-
    // gated). Let a scene disable any one system independently — the "stop Lumen" / "kill probe ambient".
    public bool GiProbesEnabled { get; set; } = true;
    public bool GiReflectionsEnabled { get; set; } = true;
    public bool GiLumenEnabled { get; set; }
}
