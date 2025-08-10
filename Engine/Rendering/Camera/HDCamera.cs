using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class HDCamera : Behaviour, IViewProjectionProvider
{
    IWindow window;
    const float nearPlane = 0.1f;
    const float farPlane = 1000.0f;
    HDRenderer renderer;

    protected internal override void OnBegin()
    {
        window = Window.Current;
        renderer = RenderAsset.Current.Renderer;
    }

    protected internal override void OnEnabled()
    {
        SceneManager.RenderCamera = this;
    }

    protected internal override void OnDisabled()
    {
        if (SceneManager.RenderCamera.Equals(this))
            SceneManager.RenderCamera = null;
    }

    public Matrix4 GetViewMatrix()
    {
        return Matrix4.LookAt(transform.Position, transform.Position + transform.Forward, transform.Up);
    }

    public Vector3 AmbientColor =>
        baseAmbientColor * LightIntensity;

    Vector3 baseAmbientColor = new Vector3(0.1f, 0.1f, 0.15f);

    protected internal override void Tick(in float delta)
    {
        if (Input.IsKeyPressed(Keys.Up))
        {
            LightIntensity += .05f;
        }

        if (Input.IsKeyPressed(Keys.Down))
        {
            LightIntensity -= .05f;
        }
    }

    float LightIntensity { get; set; } = 1.0f;

    public Matrix4 GetProjectionMatrix()
    {
        return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45.0f),
            (float)window.Width / window.Height,
            nearPlane, farPlane);
    }

    internal void RenderCamera()
    {
        renderer.BeginRender(new RendererArgs(viewProjectionProvider: this));
        renderer.PostRenderCleanUp();
    }
}