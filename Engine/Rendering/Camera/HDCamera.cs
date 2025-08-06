using OpenTK.Graphics.ES11;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class HDCamera : Behaviour, IViewProjectionProvider {
    IWindow window;
    float nearPlane = 0.1f;
    float farPlane = 100.0f;
    EngineConfiguration config;

    protected internal override void OnBegin() {
        window = BEngineEntry.Window;
        config = Service<EngineConfiguration>.Get();
    }

    protected internal override void OnEnabled() {
        SceneManager.RenderCamera = this;
    }

    protected internal override void OnDisabled() {
        if (SceneManager.RenderCamera.Equals(this))
            SceneManager.RenderCamera = null;
    }

    public Matrix4 GetViewMatrix() {
        return Matrix4.LookAt(transform.Position, transform.Position + transform.Forward, transform.Up);
    }

    public Matrix4 GetProjectionMatrix() {
        return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45.0f),
            (float)window.Width / window.Height,
            nearPlane, farPlane);
    }

    internal void RenderCamera() {
        config.Renderer.Render(RuntimeSet<IRenderTarget>.ReadOnlyCollection, new RenderArgs(viewProjectionProvider: this));
    }
}