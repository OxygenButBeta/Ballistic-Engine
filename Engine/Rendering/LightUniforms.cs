namespace BallisticEngine;

public readonly struct LightUniforms {
    public readonly Vector3 Direction;
    public readonly Vector3 Color;
    public readonly float AmbientIntensity;

    public LightUniforms(Vector3 direction, Vector3 color, float ambientIntensity) {
        Direction = direction;
        Color = color;
        AmbientIntensity = ambientIntensity;
    }

    public static LightUniforms Resolve() {
        DirectionalLight light = DirectionalLight.Instance;
        if (light is null) return new LightUniforms(Vector3.UnitY, Vector3.Zero, 0f);

        return new LightUniforms(
            -light.transform.Forward,
            light.PhysicalColor,
            light.ambientIntensity);
    }
}
