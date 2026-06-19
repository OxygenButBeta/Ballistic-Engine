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

// Reflections technique. NOTE: ScreenSpace/RayTraced keep their original ordinals (0/1)
// so existing .volume profiles that stored the enum BY VALUE still resolve; Off is appended last (=2).
public enum ReflectionMode {
    ScreenSpace,   // SSR — fast, screen-bounded
    RayTraced,     // DXR — off-screen + sky reflect correctly (falls back to SSR without DXR)
    Off,           // no reflections (IBL/skybox reflection only)
}

// Ambient-occlusion quality (the AmbientOcclusion volume's Quality dropdown). Drives the GTAO slice +
// step counts (more = smoother, fewer artefacts, costlier). Ordinals are stable for .volume by-value.
public enum AoQuality {
    Low,    // 2 slices × 4 steps  — cheapest, more noise (the denoiser/TAA leans on it)
    Medium, // 3 slices × 6 steps  — the default balance
    High,   // 4 slices × 8 steps  — clean on most content
    Ultra,  // 6 slices × 12 steps — reference quality
}

// Ambient-occlusion render resolution (the AmbientOcclusion volume's Resolution dropdown). The GTAO pass
// runs at this fraction of the render resolution and is bilinear-upsampled when the deferred pass samples it.
public enum AoResolution {
    Full,    // 1:1 — sharpest, costliest
    Half,    // 1/2 per axis (1/4 the pixels) — the default
    Quarter, // 1/4 per axis (1/16 the pixels) — cheapest
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
    // V1: re-anchored for the lux-scaled DX12 radiance (the meter's LuxMeterAnchor is +8, ~6 stops above the
    // old cd/m² anchor). A correctly-exposed lux-calibrated scene meters to EV~16; this window brackets that
    // (dark scenes open to ~13 = M~1.4e-4, bright scenes stop down to ~19) instead of the old [8,17] that let
    // dark scenes open to EV8 = M~3.3e-3 and blow out (CornellBox/LightTest). Day↔night still spans the window.
    public float AutoExposureLimitMin { get; set; } = 13f;          // EV floor the meter may reach
    public float AutoExposureLimitMax { get; set; } = 19f;          // EV ceiling the meter may reach
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
    // Half-width (in luminance) of the soft-knee transition band under the threshold. Smaller = harder
    // cutoff (only genuinely-bright pixels bloom, no scene-wide glow); larger = softer ramp-in.
    public float BloomKnee { get; set; } = 0.5f;

    // Ambient occlusion. The DX12 backend runs GTAO (ground-truth AO, Jimenez 2016) and applies it to the
    // INDIRECT (IBL ambient) term only — direct sun/punctual light is untouched, which is the physically
    // correct layer (the old HBAO post-multiplied the whole HDR colour, darkening direct light too).
    public bool SSAOEnabled { get; set; } = true;
    // World-space sampling radius. Larger reads architectural crevices (window recesses, cornices, arches);
    // 0.5 (the old default) was tuned for tabletop props and left large surfaces flat.
    public float SSAORadius { get; set; } = 1.75f; // world units
    public float SSAOIntensity { get; set; } = 1.0f;  // GTAO is physically normalized — 1 is the neutral strength
    public float SSAOPower { get; set; } = 1.0f;      // contrast/falloff exponent on the occlusion (1 = linear)
    public float SSAOThickness { get; set; } = 0.25f; // assumed occluder thickness (m): thin lets light past railings/foliage
    public bool SSAOMultiBounce { get; set; } = true; // Jimenez albedo-aware multi-bounce (avoids over-darkening dark crevices)
    public AoQuality SSAOQuality { get; set; } = AoQuality.Medium;   // slice/step count preset
    public AoResolution SSAOResolution { get; set; } = AoResolution.Half; // render fraction (Full/Half/Quarter)

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
    // Off by default: specular comes from the IBL/skybox cube unless a scene explicitly enables SSR/RT reflections.
    public bool SsrEnabled { get; set; } = false;
    public float SsrIntensity { get; set; } = 1f;
    public ReflectionMode ReflectionMode { get; set; } = ReflectionMode.Off;  // realtime SSR/RT reflections off by default

    // Volumetric height fog + sun scattering (god-rays): physical exponential height fog
    // marched against the directional shadow map. In-scatters the atmosphere-attenuated sun
    // and the baked sky's average radiance (skylight); its transmittance EXTINGUISHES the
    // scene behind it (real fog hides things). Half-res march + temporal denoise; like
    // SSR it reconstructs from the single-sample depth, so it only runs while TAA is
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
    public Vector3 VolumetricTint { get; set; } = Vector3.One; // in-scatter colour grade

    // God rays / light shafts: an AESTHETIC layer on top of the fog raymarch. The fog stays physical;
    // the shafts get their OWN visibility density (ShaftDensity) decoupled from the fog extinction, so
    // you get crisp sun shafts WITHOUT having to crank the fog to non-physical values. Shadow-gated sun
    // in-scatter, scaled by an independent decay-with-distance and a tighter phase. OFF by default → no
    // contribution unless a VolumetricLighting override turns them on (byte-identical otherwise).
    public bool ShaftsEnabled { get; set; }                           // master toggle for the aesthetic shaft layer
    public float ShaftIntensity { get; set; } = 1f;                   // overall shaft brightness multiplier
    public float ShaftDensity { get; set; } = 0.05f;                  // shaft visibility weight (1/m), INDEPENDENT of the fog density
    public float ShaftDecay { get; set; }                             // 1/m fade with march distance (0 = no fade; higher = near shafts only)
    public float ShaftSharpness { get; set; } = 0.85f;                // shaft phase anisotropy g (tighter/brighter toward the sun)
    public Vector3 ShaftTint { get; set; } = Vector3.One;             // colour grade on the shafts only

    // Volumetric dust: procedural sun-lit motes floating in the air around the camera (no scene objects;
    // a 3D noise field sampled along the same raymarch). Shadow-gated so dust only sparkles where the sun
    // reaches, drifts over time. OFF by default; animated by Time (frozen to 0 under deterministic capture
    // so paused captures stay byte-identical).
    public bool DustEnabled { get; set; }                             // master toggle for floating dust motes
    public float DustIntensity { get; set; } = 0.5f;                  // overall dust glow multiplier
    public float DustSize { get; set; } = 0.5f;                       // noise frequency scale: lower = larger/sparser motes, higher = fine/dense
    public Vector3 DustDrift { get; set; } = new(0.15f, 0.08f, 0.05f);// world-space drift velocity (m/s) of the dust field (gentle air current)
    public float DustSparkle { get; set; } = 1f;                      // how strongly motes catch the sun (twinkle gain)

    // Aerial perspective: atmospheric distance haze on opaque geometry, baked from a Hillaire froxel
    // volume that shares the ProceduralSky atmosphere (so geometry fades into the same colour as the
    // sky behind it). ON by default at a calibrated strength; only applies while a ProceduralSky is
    // active. Replaced the old ad-hoc analytic AP (the blue-white veil) — see dx12-aerial-perspective-rework.
    public bool AerialPerspectiveEnabled { get; set; } = true;
    public float AerialPerspectiveIntensity { get; set; } = 1f;        // master strength (1 = physical against the sky)
    public float AerialPerspectiveStartDistance { get; set; } = 30f;   // m: haze starts building beyond this (foreground/interiors stay crisp)
    public float AerialPerspectiveMaxDistance { get; set; } = 2000f;   // m: froxel volume far depth; haze ~half strength near 40% of this
    public float AerialPerspectiveDensityScale { get; set; } = 1f;     // apparent atmosphere density for the in-scene march (1 = physical)
    public Vector3 AerialPerspectiveTint { get; set; } = Vector3.One;  // in-scatter colour grade (extinction stays neutral)

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

    // Ray-traced sun shadows (DX12 + DXR only): trace a shadow ray per pixel against the scene BVH instead
    // of the cascaded shadow maps. Off by default (opt-in via the Shadows volume; falls back to cascades
    // on non-RT GPUs).
    public bool RayTracedShadows { get; set; }

    // Stylistic extras, all neutral/off by default.
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public float VignetteStrength { get; set; }
    public float VignetteRoundness { get; set; } = 1f;  // 1 = circular, 0 = aspect-following oval
    public Vector3 VignetteColor { get; set; } = Vector3.Zero; // darken toward black by default
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

    // The old realtime-GI and baked-GI settings were removed with the GI renderer.

    // --- Lumen V2 global illumination (the GlobalIllumination volume → these fields → the DX12 Lumen GI pass).
    // Lumen is the product GI path (HW-RT diffuse one-/multi-bounce + surface-card radiance cache). LumenEnabled
    // is the master on/off; default ON (the pass also hard-gates on hardware ray tracing — no HW RT = no GI,
    // no hidden screen-space fallback). The dials below were env-only during the Lumen build; the volume now
    // drives them, with the BALLISTIC_DX12_LUMEN_* env doors still overriding for A/B. ---
    public bool LumenEnabled { get; set; } = true;
    public float LumenIntensity { get; set; } = 2f;          // master GI strength (tuned: visible indirect without washing out)
    public float LumenSkyIntensity { get; set; } = 1.5f;     // skylight let in through open sky-visibility
    public int LumenRayCount { get; set; } = 16;             // hemisphere rays per pixel (temporal accumulation cleans the rest)
    public int LumenDenoisePasses { get; set; } = 3;         // à-trous spatial denoise iterations (0 = raw)
    public bool LumenMultiBounce { get; set; } = true;       // accumulate multi-bounce in the radiance cache
    public bool LumenReflections { get; set; } = true;       // feed RT reflections from the radiance cache
    // How much the screen-space GTAO bites the GI's contact shading. DEFAULT 0: screen-space GTAO dragged a dark
    // "ghost of nearby geometry" smudge under camera motion, and the RT trace already carries macro occlusion, so
    // mixing GTAO in double-darkened. 0 = GI sees only its own RT occlusion (+ baked material AO); opt back in per
    // scene if wanted.
    public float LumenAoStrength { get; set; } = 0f;
}
