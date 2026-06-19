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

        // VolumetricLighting supersedes VolumetricFog: same physical fog (reuses the fx.Volumetric* fields the
        // fog pass already consumes) PLUS the independent god-ray and dust layers. Applied AFTER the legacy
        // VolumetricFog block so when both somehow coexist in a profile, the unified override wins.
        if (stack.GetComponent<VolumetricLighting>() is { } vlit) {
            fx.VolumetricEnabled = vlit.enabled.Value;
            fx.VolumetricIntensity = vlit.intensity.Value;
            fx.VolumetricDensity = vlit.density.Value;
            fx.VolumetricHeightFalloff = vlit.heightFalloff.Value;
            fx.VolumetricBaseHeight = vlit.baseHeight.Value;
            fx.VolumetricScattering = vlit.scattering.Value;
            fx.VolumetricAmbientScatter = vlit.ambientScatter.Value;
            fx.VolumetricAnisotropy = vlit.anisotropy.Value;
            fx.VolumetricSunGlow = vlit.sunGlow.Value;
            fx.VolumetricSunGlowSharpness = vlit.sunGlowSharpness.Value;
            fx.VolumetricStepCount = vlit.stepCount.Value;
            fx.VolumetricMaxDistance = vlit.maxDistance.Value;
            fx.VolumetricTint = vlit.tint.Value;

            fx.ShaftsEnabled = vlit.shaftsEnabled.Value;
            fx.ShaftIntensity = vlit.shaftIntensity.Value;
            fx.ShaftDensity = vlit.shaftDensity.Value;
            fx.ShaftDecay = vlit.shaftDecay.Value;
            fx.ShaftSharpness = vlit.shaftSharpness.Value;
            fx.ShaftTint = vlit.shaftTint.Value;

            fx.DustEnabled = vlit.dustEnabled.Value;
            fx.DustIntensity = vlit.dustIntensity.Value;
            fx.DustSize = vlit.dustSize.Value;
            fx.DustDrift = vlit.dustDrift.Value;
            fx.DustSparkle = vlit.dustSparkle.Value;
        }

        if (stack.GetComponent<GlobalIllumination>() is { } gi) {
            fx.LumenEnabled = gi.enabled.Value;
            fx.LumenIntensity = gi.intensity.Value;
            fx.LumenSkyIntensity = gi.skyIntensity.Value;
            fx.LumenRayCount = gi.rayCount.Value;
            fx.LumenDenoisePasses = gi.denoisePasses.Value;
            fx.LumenMultiBounce = gi.multiBounce.Value;
            fx.LumenReflections = gi.reflections.Value;
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
