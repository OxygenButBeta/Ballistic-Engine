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
            // LUMEN GI + REFLECTIONS HARD-DISABLED (2026-06-17/18): the whole indirect-lighting stack hosted by
            // this volume is taken out of the system. The DIFFUSE side (SSGI, RT-GI, the DDGI world cache, the
            // screen probes, emissive-as-GI) was disabled 2026-06-17; the SPECULAR side (SSR + RT-reflections)
            // was disabled 2026-06-18. This bridge no longer maps the volume's GI/Reflections dropdowns onto
            // PostFX — it writes the OFF state unconditionally, so a scene whose GlobalIllumination volume has
            // GI or reflections turned on can no longer bring the dead passes back to life. The volume UI stays
            // (no code deleted) but is inert. To restore, map the dials back to the fx fields below.

            // Diffuse GI — forced off (system disabled). The advanced bounce/temporal/look dials are still
            // copied so the inert values stay coherent, but nothing consumes them while GiMode == Off.
            fx.GiMode = GiMode.Off;
            fx.SsgiEnabled = false;
            fx.SsgiIntensity = gi.intensity.Value;
            fx.SsgiDebugView = false;
            fx.GiEmissive = false;
            fx.Ddgi = false;
            fx.ScreenProbes = false;
            // Reflections (SSR + RT) — forced off (system disabled). SsrEnabled=false makes Dx12ReflectionsPass
            // .Enabled() return false, so neither the SSR march nor the RT-reflections branch ever runs.
            fx.ReflectionMode = ReflectionMode.Off;
            fx.SsrEnabled = false;
            fx.SsrIntensity = gi.reflectionsIntensity.Value;
            // Advanced bounce / temporal / look dials (inert while GI is disabled; copied for coherence).
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
