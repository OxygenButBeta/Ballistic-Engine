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
        // World-space, not local: the camera may be PARENTED (e.g. under a player controller), so it
        // must render from where it actually is in the world, not from its local offset near the
        // parent's origin. WorldRotation drives the basis so look direction follows the parent too.
        // Render-thread-safe: on the decoupled render thread these read the FROZEN published matrix; on the
        // single-threaded path they fall through to the live WorldPosition/WorldRotation (byte-identical).
        Vector3 eye = transform.RenderWorldPosition;
        Quaternion worldRotation = transform.RenderWorldRotation;
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, worldRotation);
        Vector3 up = Vector3.Transform(Vector3.UnitY, worldRotation);
        return BMatrix.LookAt(eye, eye + forward, up);
    }

    public Vector3 AmbientColor =>
        baseAmbientColor * LightIntensity;

    public Transform Transform => transform;

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
        return BMatrix.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45.0f),
            (float)window.Width / window.Height,
            nearPlane, farPlane);
    }

    internal void RenderCamera()
    {
        // Lazy init: OnBegin only fires in play mode, but a paused/headless host (screenshot
        // verification) registers this camera and renders without ever entering play.
        window ??= Window.Current;
        renderer ??= RenderAsset.Current.Renderer;
        renderer.BeginRender(new RendererArgs(viewProjectionProvider: this));
        renderer.PostRenderCleanUp();
    }

    public override void OnDrawGizmos(IGizmos gizmos)
    {
        gizmos.Color = new Vector3(0.5f, 0.8f, 1f);
        gizmos.DrawIcon(transform.Position, GizmoIcon.Camera);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos)
    {
        // A view-frustum wireframe reconstructed from the camera's fov/near/far. The real aspect
        // depends on the game window at runtime; 16:9 is a representative preview shape (do NOT call
        // GetProjectionMatrix here — it reads the live OS window size, not the editor viewport).
        gizmos.Color = new Vector3(0.5f, 0.8f, 1f);

        const float aspect = 16f / 9f;
        float tanV = MathF.Tan(MathHelper.DegreesToRadians(45f) * 0.5f);
        float gizmoFar = MathF.Min(farPlane, 30f); // cap so the frustum stays a usable size

        Vector3 pos = transform.Position;
        Vector3 fwd = transform.Forward, up = transform.Up, right = transform.Right;

        Span<Vector3> near = stackalloc Vector3[4];
        Span<Vector3> far = stackalloc Vector3[4];
        Corners(pos, fwd, up, right, nearPlane, tanV, aspect, near);
        Corners(pos, fwd, up, right, gizmoFar, tanV, aspect, far);

        for (var i = 0; i < 4; i++)
        {
            int n = (i + 1) % 4;
            gizmos.DrawLine(near[i], near[n]); // near rectangle
            gizmos.DrawLine(far[i], far[n]);   // far rectangle
            gizmos.DrawLine(near[i], far[i]);  // connecting edge
        }
    }

    static void Corners(Vector3 pos, Vector3 fwd, Vector3 up, Vector3 right,
        float dist, float tanV, float aspect, Span<Vector3> outCorners)
    {
        float h = tanV * dist;
        float w = h * aspect;
        Vector3 c = pos + fwd * dist;
        outCorners[0] = c + up * h - right * w; // top-left
        outCorners[1] = c + up * h + right * w; // top-right
        outCorners[2] = c - up * h + right * w; // bottom-right
        outCorners[3] = c - up * h - right * w; // bottom-left
    }
}