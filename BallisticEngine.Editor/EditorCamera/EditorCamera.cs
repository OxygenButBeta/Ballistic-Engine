using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// A free-fly camera the editor uses to view the scene, independent of any HDCamera in the scene.
// Drives the renderer as an IViewProjectionProvider. RMB-look + WASDQE move, scroll adjusts speed.
// Aspect comes from the viewport panel, not the OS window.
internal sealed class EditorCamera : IViewProjectionProvider {
    readonly Transform transform = new();

    float pitch;
    float yaw;
    float moveSpeed = EditorPrefs.Current.CameraBaseSpeed;
    float aspect = 16f / 9f;

    const float nearPlane = 0.1f;
    const float farPlane = 1000f;
    const float fovDegrees = 45f;
    const float sensitivity = 0.2f;

    public EditorCamera() {
        transform.Position = new Vector3(0, 0, -12);
    }

    public Transform Transform => transform;
    public Vector3 AmbientColor => Vector3.One * 0.1f;

    // Move so the target fills a comfortable portion of the view, keeping the current look direction.
    public void Focus(Vector3 target, float radius) {
        transform.Position = target - transform.Forward * Math.Max(2f, radius * 3f);
    }

    // Snap the camera to look along `direction` (e.g. the orientation gizmo's front/top/side axes),
    // orbiting around the point it currently looks at so the framed content stays put. Derives
    // yaw/pitch as the inverse of the Update() convention (Forward = qYaw * qPitch * UnitZ, i.e.
    // Forward = (cosP*sinY, -sinP, cosP*cosY)).
    public void LookDirection(Vector3 direction) {
        if (direction.LengthSquared < 1e-6f)
            return;
        direction = direction.Normalized();

        // Keep looking at the same focus point (a fixed distance ahead) after re-orienting.
        const float orbitDistance = 12f;
        Vector3 focus = transform.Position + transform.Forward * orbitDistance;

        pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(-direction.Y, -1f, 1f)));
        pitch = MathHelper.Clamp(pitch, -90f, 90f);
        yaw = MathHelper.RadiansToDegrees(MathF.Atan2(direction.X, direction.Z));

        Quaternion qPitch = Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(pitch));
        Quaternion qYaw = Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(yaw));
        transform.Rotation = qYaw * qPitch;

        transform.Position = focus - transform.Forward * orbitDistance;
    }

    public void SetAspect(float panelAspect) {
        if (panelAspect > 0f)
            aspect = panelAspect;
    }

    public Matrix4 GetViewMatrix() =>
        Matrix4.LookAt(transform.Position, transform.Position + transform.Forward, transform.Up);

    public Matrix4 GetProjectionMatrix() =>
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(fovDegrees), aspect, nearPlane, farPlane);

    bool flying;

    // Unity-style fly-cam: hold RMB over the Scene view to look around and move with WASDQE.
    // Once flying, control persists while RMB stays held, even if the cursor leaves the panel.
    public void Update(float dt, bool hovered, EditorInput input) {
        flying = input.RightMouseDown && (flying || hovered);
        if (!flying)
            return;

        if (input.ScrollY > 0) moveSpeed += 1f;
        else if (input.ScrollY < 0) moveSpeed = Math.Max(1f, moveSpeed - 1f);

        yaw -= input.MouseDelta.X * sensitivity;
        pitch += input.MouseDelta.Y * sensitivity;
        pitch = MathHelper.Clamp(pitch, -89f, 89f);

        Quaternion qPitch = Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(pitch));
        Quaternion qYaw = Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(yaw));
        transform.Rotation = qYaw * qPitch;

        Vector3 direction = Vector3.Zero;
        if (input.Key(EditorKey.W)) direction += transform.Forward;
        if (input.Key(EditorKey.S)) direction -= transform.Forward;
        if (input.Key(EditorKey.D)) direction -= transform.Right;
        if (input.Key(EditorKey.A)) direction += transform.Right;
        if (input.Key(EditorKey.E)) direction += transform.Up;
        if (input.Key(EditorKey.Q)) direction -= transform.Up;

        float speed = moveSpeed * (input.Key(EditorKey.Shift) ? 2f : 1f);
        if (direction != Vector3.Zero)
            transform.Position += direction.Normalized() * speed * dt;
    }
}
