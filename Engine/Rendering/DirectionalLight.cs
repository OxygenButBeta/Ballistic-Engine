using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class DirectionalLight : Behaviour
{
    public static DirectionalLight Instance;
    public Vector3 AmbientLight => _ambientColor * ambientIntensity;
    Vector3 _ambientColor = new Vector3(0.35f, 0.40f, 0.45f);
    float ambientIntensity = .3f;
    public Vector3 LightColor => _lightColor * LightIntensity;
    readonly Vector3 _lightColor = new Vector3(1.0f, 0.95f, 0.85f);
    float LightIntensity = 5f;


    protected internal override void OnBegin()
    {
        Instance = this;
    }

    protected internal override void Tick(in float delta)
    {
        if (Input.IsKeyDown(Keys.U))
        {
            LightIntensity += 0.01f;
        }
        else if (Input.IsKeyDown(Keys.L))
        {
            LightIntensity -= 0.01f;
        }

        if (Input.IsKeyDown(Keys.Q))
        {
            ambientIntensity += 0.001f;
        }
        else if (Input.IsKeyDown(Keys.E))
        {
            ambientIntensity -= 0.001f;
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