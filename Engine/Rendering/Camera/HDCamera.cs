using OpenTK.Mathematics;

namespace BallisticEngine;

public class HDCamera : Behaviour, IViewProjectionProvider
{
    IWindow window;
    readonly float nearPlane = 0.1f;
    readonly float farPlane = 100.0f;
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

    public Matrix4 GetProjectionMatrix()
    {
        return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45.0f),
            (float)window.Width / window.Height,
            nearPlane, farPlane);
    }

    internal void RenderCamera()
    {
        renderer.Render(RuntimeSet<IRenderTarget>.ReadOnlyCollection,
            new RenderArgs(viewProjectionProvider: this));
    }
}