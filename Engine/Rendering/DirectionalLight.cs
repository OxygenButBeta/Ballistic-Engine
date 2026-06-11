using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// CPU-side lighting values the renderer pushes as uniforms. Built from the active
// DirectionalLight, or from sensible defaults when a scene has no light (edit mode / empty scene).
public readonly struct LightUniforms {
    public readonly Vector3 Direction;   // toward the light (-forward)
    public readonly Vector3 Color;       // intensity * color
    public readonly float AmbientIntensity;

    public LightUniforms(Vector3 direction, Vector3 color, float ambientIntensity) {
        Direction = direction;
        Color = color;
        AmbientIntensity = ambientIntensity;
    }

    public static LightUniforms Resolve() {
        DirectionalLight light = DirectionalLight.Instance;
        if (light is null)
            return new LightUniforms(Vector3.UnitY, new Vector3(1f, 0.95f, 0.85f) * 3f, 0.3f);

        return new LightUniforms(
            -light.transform.Forward,
            light.LightIntensity * light.LightColor,
            light.ambientIntensity);
    }
}

public class DirectionalLight : Behaviour
{
    public static DirectionalLight Instance;
    public Vector3 AmbientLight => _ambientColor * ambientIntensity;
    Vector3 _ambientColor = new(0.35f, 0.40f, 0.45f);
    public float ambientIntensity = .3f;
    public Vector3 LightColor => _lightColor * LightIntensity;
    readonly Vector3 _lightColor = new(1.0f, 0.95f, 0.85f);
    public float LightIntensity = 5f;

    // How far from the camera the directional shadow map reaches (world units).
    public float ShadowDistance = 60f;
    public float ShadowBias = 0.0015f;

    // Register on attach so edit mode is lit/shadowed by the scene light too, not just play mode.
    protected internal override void OnAttach()
    {
        Instance = this;
    }

    protected internal override void OnDetach()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    protected internal override void OnBegin()
    {
        Instance = this;
    }

    protected internal override void Tick(in float delta)
    {
        if (Input.IsKeyDown(Keys.U))
        {
            LightIntensity += 0.1f;
        }
        else if (Input.IsKeyDown(Keys.L))
        {
            LightIntensity -= 0.1f;
        }

        if (Input.IsKeyDown(Keys.Q))
        {
            ambientIntensity += 0.02f;
        }
        else if (Input.IsKeyDown(Keys.E))
        {
            ambientIntensity -= 0.02f;
        }


        Vector3 angles = transform.EulerAngles; // degrees

        float speed = 45f * delta;

        if (Input.IsKeyDown(Keys.Right))
            angles.Y -= speed;
        if (Input.IsKeyDown(Keys.Left))
            angles.Y += speed;
        if (Input.IsKeyDown(Keys.Up))
            angles.X -= speed;
        if (Input.IsKeyDown(Keys.Down))
            angles.X += speed;

        transform.EulerAngles = angles;
    }
}
