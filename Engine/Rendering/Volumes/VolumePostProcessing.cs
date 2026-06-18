namespace BallisticEngine;

// Maps the blended VolumeStack onto the renderer's live PostProcessSettings each frame.
// This is the ONLY seam between the generic volume framework and the GL pipeline's flat
// settings object — new VolumeComponents extend this mapping, nothing else.
public static class VolumePostProcessing {
    public static void Apply(VolumeStack stack, PostProcessSettings fx) {
        if (stack.GetComponent<Exposure>() is { } exposure) {
            fx.ExposureMode = exposure.mode.Value;
            fx.ExposureEV = exposure.exposureEV.Value;
            fx.ExposureCompensation = exposure.compensation.Value;
            fx.Exposure = exposure.multiplier.Value;
            fx.MeteringMode = exposure.metering.Value;
            fx.AutoExposureLimitMin = exposure.limitMin.Value;
            fx.AutoExposureLimitMax = exposure.limitMax.Value;
            fx.AutoExposureSpeedDarkToLight = exposure.speedDarkToLight.Value;
            fx.AutoExposureSpeedLightToDark = exposure.speedLightToDark.Value;
            fx.HistogramFilterMin = exposure.histogramMin.Value;
            fx.HistogramFilterMax = exposure.histogramMax.Value;
        }

        if (stack.GetComponent<Bloom>() is { } bloom) {
            fx.BloomEnabled = bloom.enabled.Value;
            fx.BloomIntensity = bloom.intensity.Value;
            fx.BloomThreshold = bloom.threshold.Value;
            fx.BloomKnee = bloom.knee.Value;
        }

        if (stack.GetComponent<AmbientOcclusion>() is { } ao) {
            fx.SSAOEnabled = ao.enabled.Value;
            fx.SSAOQuality = ao.quality.Value;
            fx.SSAOResolution = ao.resolution.Value;
            fx.SSAORadius = ao.radius.Value;
            fx.SSAOIntensity = ao.intensity.Value;
            fx.SSAOPower = ao.power.Value;
            fx.SSAOThickness = ao.thickness.Value;
            fx.SSAOMultiBounce = ao.multiBounce.Value;
        }

        if (stack.GetComponent<AntiAliasing>() is { } aa) {
            fx.TaaEnabled = aa.taaEnabled.Value;
            fx.TaaFeedback = aa.taaFeedback.Value;
            fx.MsaaSamples = aa.msaaSamples.Value;
        }

        if (stack.GetComponent<Upscaling>() is { } upscale) {
            fx.UpscaleMode = upscale.mode.Value;
            fx.UpscaleSharpness = upscale.sharpness.Value;
        }

        // THE unified Global Illumination volume — indirect light only (diffuse GI + reflections).
        // Replaced the old ScreenSpaceGlobalIllumination + ScreenSpaceReflections + the dead GL
        // probe/Lumen split overrides (P0.5 consolidation). The two Mode dropdowns each carry their own
        // Off, so the enable bool is derived from the mode (no separate enable param to keep in sync).
        if (stack.GetComponent<GlobalIllumination>() is { } gi) {
            // === GI PRAGMATIC REVIVAL R0.1 (2026-06-18) — bridge FLIPPED to volume-driven. ===
            // This bridge is the shipping front door: the blended GlobalIllumination volume drives PostFX, which
            // the renderer reads each frame. The 2026-06-17/18 hard-disable (this block unconditionally writing
            // GiMode.Off / SsgiEnabled=false / ReflectionMode.Off / SsrEnabled=false) is REVERTED — the volume's
            // GI/Reflections dropdowns now map onto PostFX so a scene whose GlobalIllumination volume turns GI or
            // reflections on actually gets them. (The volume `enabled` master switch hard-stops the whole stack:
            // enabled=false forces both modes Off regardless of the dropdowns — matches the component tooltip.)
            //
            // PRECEDENCE (defined here, R0.1 — not deferred to R3): the VOLUME is AUTHORITATIVE; it drives PostFX
            // unconditionally below. The BALLISTIC_DX12_* env doors are a DEBUG OVERRIDE only — they win over
            // PostFX at the renderer choke point (DX12HDRenderer GI-mode resolve: BALLISTIC_DX12_SSGI/RT_GI for
            // diffuse, BALLISTIC_DX12_RT_REFLECTIONS for the SSR-vs-RT reflection branch), NOT here in the bridge.
            // So the volume is the user/shipping path; an env door is the A/B-harness / bisect override layered on
            // top. The no-RT auto-downgrade (RayTraced→ScreenSpace without HW RT) also lives at that choke point.
            //
            // ⚠ DEV-ONLY, R1.0-INCOMPLETE: GI is back on the shipping path but RT-GI / emissive-as-GI bounce is
            // still gated on per-triangle MaterialId only present on submesh-range meshes (R1.0 moves it into the
            // RT geometry build). Until R1.0 lands, color-only / whole-mesh content gets NO RT bounce/bleed.
            bool giOn = gi.enabled.Value;
            fx.GiMode = giOn ? gi.giMode.Value : GiMode.Off;
            fx.SsgiEnabled = giOn && gi.giMode.Value != GiMode.Off;
            fx.SsgiIntensity = gi.intensity.Value;
            fx.SsgiDebugView = giOn && gi.giIsolate.Value;
            fx.GiEmissive = giOn && gi.emissiveAsGi.Value;
            fx.Ddgi = giOn && gi.worldRadianceCache.Value;
            fx.ScreenProbes = giOn && gi.screenProbes.Value;
            // Reflections (SSR + RT) — driven by the volume's Reflections-Mode dropdown. SsrEnabled gates
            // Dx12ReflectionsPass.Enabled(); ReflectionMode selects the SSR vs RT branch inside Record.
            fx.ReflectionMode = giOn ? gi.reflectionsMode.Value : ReflectionMode.Off;
            fx.SsrEnabled = giOn && gi.reflectionsMode.Value != ReflectionMode.Off;
            fx.SsrIntensity = gi.reflectionsIntensity.Value;
            // Advanced bounce / temporal / look dials (consumed by the SSGI/GI combine when GI is on; copied
            // always so the values stay coherent when GI is toggled mid-session).
            fx.SsgiRayLength = gi.rayLength.Value;
            fx.SsgiFalloff = gi.falloff.Value;
            fx.SsgiThickness = gi.thickness.Value;
            fx.SsgiBounceBoost = gi.bounceBoost.Value;
            fx.SsgiOcclusionPower = gi.occlusionPower.Value;
            fx.SsgiRayCount = gi.rayCount.Value;
            fx.SsgiMaxHistory = gi.maxHistory.Value;
            fx.SsgiLook = gi.look.Value;
            fx.SsgiTint = gi.tint.Value;
            fx.SsgiSaturation = gi.saturation.Value;
            // === GI PRAGMATIC REVIVAL R3.2 (2026-06-18) — GiQuality preset DRIVES the effective dials. ===
            // GiMode + GiQuality are THE two control surfaces for GI (plan §2 R3.2 DoD: "GI behavior changes
            // ONLY via GiMode + GiQuality"). The preset is a fixed assignment over the EXISTING dials — no new
            // technique, no PostFX.GiQuality field (the renderer reads the resolved dials, not the preset). It
            // is applied AFTER the per-dial copies above, so the preset is the authoritative effective value;
            // the user-overridable Advanced foldout that lets a dial escape the preset is the POST-PLAN
            // follow-up (NOT this chunk). Preset values are the R2.1 tables (gi-revival-R0-baseline.md §R2.1(C)):
            //   High (RTX 2060 ship target) = 4 slices / 24 history — IDENTICAL to the engine defaults, so a
            //     scene at the default High renders byte-identically to HEAD (the volume-framework contract).
            //   Epic (RTX 3070+)            = 8 slices / 32 history — more slices + longer temporal accumulation.
            // The GiMode=RayTraced+gather-only / denser DDGI round-robin / RT-refl lower-roughness-cutoff parts
            // of the Epic-vs-High end-state are the R2.2/R2.3 wiring deps (the gather-only RT-GI branch); until
            // those land the runtime preset rides the validated GPU-safe ScreenSpace path the GiMode dropdown
            // already selects, so the preset only drives the slice/history dials here.
            if (giOn) {
                switch (gi.giQuality.Value) {
                    case GiQuality.Epic:
                        fx.SsgiRayCount = 8;     // more horizon slices (clamped ≤8 in the SSGI gather)
                        fx.SsgiMaxHistory = 32f; // longer temporal accumulation (smoother, laggier — OK on Epic HW)
                        break;
                    case GiQuality.High:
                    default:
                        fx.SsgiRayCount = 4;     // == engine default → byte-identical to HEAD at default High
                        fx.SsgiMaxHistory = 24f; // == engine default
                        break;
                }
            }
        }

        if (stack.GetComponent<Shadows>() is { } shadows) {
            fx.ShadowMaxDistance = shadows.maxDistance.Value;
            fx.ShadowCascadeCount = shadows.cascadeCount.Value;
            fx.ShadowSplitDistribution = shadows.splitDistribution.Value;
            fx.ShadowCascadeBlend = shadows.cascadeBlend.Value;
            fx.ShadowResolution = shadows.resolution.Value;
            fx.ShadowFiltering = shadows.filtering.Value;
            fx.ShadowSoftness = shadows.softness.Value;
            fx.ContactShadowsEnabled = shadows.contactShadows.Value;
            fx.ContactShadowLength = shadows.contactLength.Value;
            fx.ContactShadowSteps = shadows.contactSteps.Value;
            fx.ContactShadowThickness = shadows.contactThickness.Value;
            fx.RayTracedShadows = shadows.rayTracedShadows.Value;
        }

        if (stack.GetComponent<VolumetricFog>() is { } volumetric) {
            fx.VolumetricEnabled = volumetric.enabled.Value;
            fx.VolumetricIntensity = volumetric.intensity.Value;
            fx.VolumetricDensity = volumetric.density.Value;
            fx.VolumetricHeightFalloff = volumetric.heightFalloff.Value;
            fx.VolumetricBaseHeight = volumetric.baseHeight.Value;
            fx.VolumetricScattering = volumetric.scattering.Value;
            fx.VolumetricAmbientScatter = volumetric.ambientScatter.Value;
            fx.VolumetricAnisotropy = volumetric.anisotropy.Value;
            fx.VolumetricSunGlow = volumetric.sunGlow.Value;
            fx.VolumetricSunGlowSharpness = volumetric.sunGlowSharpness.Value;
            fx.VolumetricStepCount = volumetric.stepCount.Value;
            fx.VolumetricMaxDistance = volumetric.maxDistance.Value;
            fx.VolumetricFeedback = volumetric.feedback.Value;
            fx.VolumetricTint = volumetric.tint.Value;
        }

        if (stack.GetComponent<AerialPerspective>() is { } aerial) {
            fx.AerialPerspectiveEnabled = aerial.enabled.Value;
            fx.AerialPerspectiveIntensity = aerial.intensity.Value;
            fx.AerialPerspectiveStartDistance = aerial.startDistance.Value;
            fx.AerialPerspectiveMaxDistance = aerial.maxDistance.Value;
            fx.AerialPerspectiveDensityScale = aerial.densityScale.Value;
            fx.AerialPerspectiveTint = aerial.tint.Value;
        }

        if (stack.GetComponent<ColorAdjustments>() is { } grade) {
            fx.Contrast = grade.contrast.Value;
            fx.Saturation = grade.saturation.Value;
        }

        if (stack.GetComponent<Vignette>() is { } vignette) {
            fx.VignetteStrength = vignette.intensity.Value;
            fx.VignetteRoundness = vignette.roundness.Value;
            fx.VignetteColor = vignette.color.Value;
        }

        if (stack.GetComponent<FilmGrain>() is { } grain)
            fx.FilmGrain = grain.intensity.Value;

        if (stack.GetComponent<Sharpening>() is { } sharpen)
            fx.Sharpen = sharpen.intensity.Value;

        if (stack.GetComponent<LensEffects>() is { } lens) {
            fx.ChromaticAberration = lens.chromaticAberration.Value;
            fx.LensDistortion = lens.distortion.Value;
        }

        if (stack.GetComponent<DepthOfField>() is { } dof) {
            fx.DofEnabled = dof.enabled.Value;
            fx.DofFocusDistance = dof.focusDistance.Value;
            fx.DofFocalLength = dof.focalLength.Value;
            fx.DofAperture = dof.aperture.Value;
            fx.DofMaxCoc = dof.maxBlur.Value;
        }
    }
}
