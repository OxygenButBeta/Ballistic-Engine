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
        }

        if (stack.GetComponent<AmbientOcclusion>() is { } ao) {
            fx.SSAOEnabled = ao.enabled.Value;
            fx.SSAORadius = ao.radius.Value;
            fx.SSAOIntensity = ao.intensity.Value;
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
            // MASTER SWITCH: enabled=false is a HARD STOP for the whole Lumen stack. We force every GI/
            // reflection mode to Off here (overriding the dropdowns) so EVERY downstream reader — SSGI,
            // RT-GI, SSR, RT-reflections, DDGI, screen probes, emissive-as-GI — sees "off" with no extra
            // wiring. The renderer also hard-gates DDGI/screen-probe allocation on these same fields.
            bool on = gi.enabled.Value;

            // Diffuse GI.
            fx.GiMode = on ? gi.giMode.Value : GiMode.Off;
            fx.SsgiEnabled = on && gi.giMode.Value != GiMode.Off;
            fx.SsgiIntensity = gi.intensity.Value;
            fx.SsgiDebugView = on && gi.giIsolate.Value;
            fx.GiEmissive = on && gi.emissiveAsGi.Value;   // emissive surfaces as area lights in the bounce
            // Ray-Traced GI quality (DDGI world cache + screen probes). Part of the hard kill: off when the
            // master switch is off, so the whole Lumen world/screen-probe machinery stops too.
            fx.Ddgi = on && gi.worldRadianceCache.Value;
            fx.ScreenProbes = on && gi.screenProbes.Value;
            // Reflections (Off maps to SsrEnabled=false; the renderer's SSR-vs-RT gate keeps working).
            fx.ReflectionMode = on ? gi.reflectionsMode.Value : ReflectionMode.Off;
            fx.SsrEnabled = on && gi.reflectionsMode.Value != ReflectionMode.Off;
            fx.SsrIntensity = gi.reflectionsIntensity.Value;
            // Advanced bounce / temporal / look dials (every one is consumed by the DX12 SSGI/RT-GI path).
            fx.SsgiRayLength = gi.rayLength.Value;
            fx.SsgiFalloff = gi.falloff.Value;
            fx.SsgiThickness = gi.thickness.Value;
            fx.SsgiBounceBoost = gi.bounceBoost.Value;   // shader Params1.x (was hardcoded 0)
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
