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

        if (stack.GetComponent<ScreenSpaceReflections>() is { } ssr) {
            fx.SsrEnabled = ssr.enabled.Value;
            fx.SsrIntensity = ssr.intensity.Value;
        }

        if (stack.GetComponent<ScreenSpaceGlobalIllumination>() is { } ssgi) {
            fx.SsgiEnabled = ssgi.enabled.Value;
            fx.SsgiLook = ssgi.look.Value;
            fx.SsgiDebugView = ssgi.debugView.Value;
            fx.SsgiSkyFallback = ssgi.skyFallback.Value;
            fx.SsgiRayCount = ssgi.rayCount.Value;
            fx.SsgiMaxHistory = ssgi.maxHistory.Value;
            fx.SsgiDenoise = ssgi.denoise.Value;
            fx.SsgiRayLength = ssgi.rayLength.Value;
            fx.SsgiFalloff = ssgi.falloff.Value;
            fx.SsgiThickness = ssgi.thickness.Value;
            fx.SsgiIntensity = ssgi.intensity.Value;
            fx.SsgiTint = ssgi.tint.Value;
            fx.SsgiSaturation = ssgi.saturation.Value;
            fx.SsgiOcclusionPower = ssgi.occlusionPower.Value;
            fx.SsgiMultiBounce = ssgi.multiBounce.Value;
            // Retired dials (IBL carries the ambient base now); pinned to 0, no volume override.
            fx.SsgiBounceBoost = 0f;
            fx.SsgiAmbientFloor = 0f;
        }

        // GI overrides. CRITICAL: VolumeManager.Stack ALWAYS contains EVERY VolumeComponent type at its
        // defaults (it's built from the whole component registry); GetComponent<T>() is never null. So a
        // parameter only counts as "set by a profile" when its .Overridden flag is true. Earlier this
        // applied split params on mere presence — a scene whose profile had GlobalIllumination but NOT
        // Lumen still saw the DEFAULT Lumen (enabled=false) clobber GI.sdfForceEnabled=true, so "toggling
        // Lumen did nothing" / SDF-GI never turned on. Now each split param wins ONLY when overridden,
        // and the legacy GI fills a field only when no split override claimed it.
        var lp = stack.GetComponent<LightProbes>();
        var rp = stack.GetComponent<ReflectionProbes>();
        var lumen = stack.GetComponent<Lumen>();
        var gi = stack.GetComponent<GlobalIllumination>();

        // Legacy GlobalIllumination (applies only its OVERRIDDEN params).
        if (gi is not null) {
            if (gi.ambientFloor.Overridden) fx.GiAmbientFloor = gi.ambientFloor.Value;
            if (gi.probeIntensity.Overridden) fx.GiProbeIntensity = gi.probeIntensity.Value;
            if (gi.probeDensity.Overridden) fx.GiProbeDensity = gi.probeDensity.Value;
            if (gi.debugShowProbes.Overridden) fx.GiDebugShowProbes = gi.debugShowProbes.Value;
            if (gi.reflectionIntensity.Overridden) fx.GiReflectionIntensity = gi.reflectionIntensity.Value;
            if (gi.reflectionDensity.Overridden) fx.GiReflectionDensity = gi.reflectionDensity.Value;
            if (gi.debugShowReflectionProbes.Overridden) fx.GiDebugShowReflectionProbes = gi.debugShowReflectionProbes.Value;
            if (gi.sdfIntensity.Overridden) fx.GiSdfIntensityScale = gi.sdfIntensity.Value;
            if (gi.sdfForceEnabled.Overridden) fx.GiSdfForceEnabled = gi.sdfForceEnabled.Value;
        }

        // Split overrides (preferred) — each param wins ONLY if actually overridden in a profile, so an
        // unconfigured default component can't clobber the legacy GI or another override.
        if (lp is not null) {
            if (lp.enabled.Overridden) fx.GiProbesEnabled = lp.enabled.Value;
            if (lp.intensity.Overridden) fx.GiProbeIntensity = lp.intensity.Value;
            if (lp.density.Overridden) fx.GiProbeDensity = lp.density.Value;
            if (lp.debugShow.Overridden) fx.GiDebugShowProbes = lp.debugShow.Value;
        }
        if (rp is not null) {
            if (rp.enabled.Overridden) fx.GiReflectionsEnabled = rp.enabled.Value;
            if (rp.intensity.Overridden) fx.GiReflectionIntensity = rp.intensity.Value;
            if (rp.density.Overridden) fx.GiReflectionDensity = rp.density.Value;
            if (rp.debugShow.Overridden) fx.GiDebugShowReflectionProbes = rp.debugShow.Value;
        }
        if (lumen is not null) {
            if (lumen.enabled.Overridden) { fx.GiLumenEnabled = lumen.enabled.Value; fx.GiSdfForceEnabled = lumen.enabled.Value; }
            if (lumen.intensity.Overridden) fx.GiSdfIntensityScale = lumen.intensity.Value;
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
