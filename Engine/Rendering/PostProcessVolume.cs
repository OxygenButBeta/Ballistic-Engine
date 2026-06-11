namespace BallisticEngine;

// Scene-wide post-processing control, Unity-volume style: one SceneBehaviour carries the
// toggles and values for every effect (exposure, bloom, SSAO, MSAA, grading). The renderer
// copies the active volume into its live PostProcessSettings each frame; a scene without a
// volume renders with the engine defaults.
public class PostProcessVolume : SceneBehaviour {
    public static PostProcessVolume Active { get; private set; }

    public float Exposure { get; set; } = 1f;

    public bool BloomEnabled { get; set; } = true;
    public float BloomIntensity { get; set; } = 0.04f;
    public float BloomThreshold { get; set; } = 1f;

    public bool SSAOEnabled { get; set; } = true;
    public float SSAORadius { get; set; } = 0.5f;
    public float SSAOIntensity { get; set; } = 1f;

    public int MsaaSamples { get; set; } = 4;

    // Stylistic grade, neutral by default.
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public float VignetteStrength { get; set; }
    public float FilmGrain { get; set; }
    public float Sharpen { get; set; }

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    internal void CopyTo(PostProcessSettings settings) {
        settings.Exposure = Exposure;
        settings.BloomEnabled = BloomEnabled;
        settings.BloomIntensity = BloomIntensity;
        settings.BloomThreshold = BloomThreshold;
        settings.SSAOEnabled = SSAOEnabled;
        settings.SSAORadius = SSAORadius;
        settings.SSAOIntensity = SSAOIntensity;
        settings.MsaaSamples = MsaaSamples;
        settings.Contrast = Contrast;
        settings.Saturation = Saturation;
        settings.VignetteStrength = VignetteStrength;
        settings.FilmGrain = FilmGrain;
        settings.Sharpen = Sharpen;
    }
}
