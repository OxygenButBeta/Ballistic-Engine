
namespace BallisticEngine;

public class SceneLighting : SceneBehaviour {
    public static SceneLighting Active { get; private set; }

    public Vector3 AmbientColor { get; set; } = Vector3.One;
    public float AmbientIntensity { get; set; } = 1f;

    public float ReflectionIntensity { get; set; } = 1f;

    public Vector3 ShadowColor { get; set; } = Vector3.Zero;
    public float ShadowStrength { get; set; } = 1f;

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
