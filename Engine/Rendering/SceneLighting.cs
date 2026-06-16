
namespace BallisticEngine;

// Scene-wide lighting environment (ambient term, shadow appearance, fog) as a single
// SceneBehaviour, like Unity's Environment/Lighting settings. The renderer reads the active
// instance every frame; a scene without one renders with neutral defaults.
public class SceneLighting : SceneBehaviour {
    public static SceneLighting Active { get; private set; }

    // Ambient TINT only. With physical IBL the irradiance map IS the ambient light in real
    // measured units, so AmbientIntensity is no longer a brightness fudge - it stays at 1 and
    // AmbientColor is a neutral artistic tint. Pushing brightness happens via EV exposure or
    // the sky's own luminance, not here. (Kept for backward-compat and subtle grading.)
    public Vector3 AmbientColor { get; set; } = Vector3.One;
    public float AmbientIntensity { get; set; } = 1f;

    // Environment (sky) reflection strength - the specular half of the IBL. Physical = 1.
    // The old 0.6 haircut compensated for the shader's specular occlusion applying AO twice;
    // with the single Lagarde term + multiscatter energy conservation, full strength is correct
    // and screen-space AO now feeds the same occlusion term.
    public float ReflectionIntensity { get; set; } = 1f;

    // What remains inside sun shadows. Strength: 0 = shadows disabled, 1 = full shadows.
    // ShadowColor tints the shadowed direct light (black = classic dark shadows; a dark
    // blue lifts them the way bounced sky light would).
    public Vector3 ShadowColor { get; set; } = Vector3.Zero;
    public float ShadowStrength { get; set; } = 1f;

    // Distance fog.
    public bool FogEnabled { get; set; }
    public Vector3 FogColor { get; set; } = new(0.6f, 0.7f, 0.9f);
    public float FogDensity { get; set; } = 0.0015f;

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }
}
