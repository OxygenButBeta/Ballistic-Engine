using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// CPU-side lighting values the renderer pushes as uniforms. Built from the active
// DirectionalLight, or from sensible defaults when a scene has no light (edit mode / empty scene).
public readonly struct LightUniforms {
    public readonly Vector3 Direction;   // toward the light (-forward)
    public readonly Vector3 Color;       // intensity * color
    public readonly float AmbientIntensity;
    public readonly Matrix4 LightSpaceMatrix;

    public LightUniforms(Vector3 direction, Vector3 color, float ambientIntensity, Matrix4 lightSpaceMatrix) {
        Direction = direction;
        Color = color;
        AmbientIntensity = ambientIntensity;
        LightSpaceMatrix = lightSpaceMatrix;
    }

    public static LightUniforms Resolve() {
        DirectionalLight light = DirectionalLight.Instance;
        if (light is null)
            return new LightUniforms(Vector3.UnitY, new Vector3(1f, 0.95f, 0.85f) * 3f, 0.3f, Matrix4.Identity);

        return new LightUniforms(
            -light.transform.Forward,
            light.LightIntensity * light.LightColor,
            light.ambientIntensity,
            light.GetLightSpaceMatrix());
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

   public Matrix4 GetLightSpaceMatrix() {
       Vector3 lightDir = transform.Forward.Normalized();
       Vector3 lightPos = transform.Position - lightDir * 1;

       Vector3 target = Vector3.Zero;
       Vector3 up = transform.Up;

       Matrix4 lightView = Matrix4.LookAt(lightPos, target, up);
       Matrix4 lightProjection = Matrix4.CreateOrthographic(1, 1, 0.1f, 100f);

       return lightProjection * lightView;
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