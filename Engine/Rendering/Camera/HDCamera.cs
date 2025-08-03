using OpenTK.Graphics.ES11;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class HDCamera : Behaviour
{
    IWindow window;
    float nearPlane = 0.1f;
    float farPlane = 100.0f;

    protected internal override void OnBegin()
    {
        window = BallisticEngine.Window;
    }

    protected internal override void OnEnabled()
    {
        Scene.Instance.RenderCamera = this;
    }

    protected internal override void OnDisabled()
    {
        if (Scene.Instance.RenderCamera.Equals(this))
            Scene.Instance.RenderCamera = null;
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

    float r = 0.0f, g = 0.8f, b = 0.1f;

    internal void Render()
    {
        GL.ClearColor(r, g, b, 1f);


        IInputProvider input = BallisticEngine.Input;
        // change the color by arrow keys
        if (input.IsKeyDown(Keys.Up))
            r = MathHelper.Clamp(r + 0.01f, 0.0f, 1.0f);
        if (input.IsKeyDown(Keys.Down))
            r = MathHelper.Clamp(r - 0.01f, 0.0f, 1.0f);
        if (input.IsKeyDown(Keys.Left))
            g = MathHelper.Clamp(g + 0.01f, 0.0f, 1.0f);
        if (input.IsKeyDown(Keys.Right))
            g = MathHelper.Clamp(g - 0.01f, 0.0f, 1.0f);

        if (input.IsKeyDown(Keys.Space))
        {
            IsEnabled = !IsEnabled;
        }

        // Clear the color buffer
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);
    }
}