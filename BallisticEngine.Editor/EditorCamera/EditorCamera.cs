
namespace BallisticEngine.Editor;

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

    public void Focus(Vector3 target, float radius) {
        transform.Position = target - transform.Forward * Math.Max(2f, radius * 3f);
    }

    public void LookDirection(Vector3 direction) {
        if (direction.LengthSquared() < 1e-6f)
            return;
        direction = direction.Normalized();

        const float orbitDistance = 12f;
        Vector3 focus = transform.Position + transform.Forward * orbitDistance;

        pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(-direction.Y, -1f, 1f)));
        pitch = MathHelper.Clamp(pitch, -90f, 90f);
        yaw = MathHelper.RadiansToDegrees(MathF.Atan2(direction.X, direction.Z));

        Quaternion qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(pitch));
        Quaternion qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(yaw));
        transform.Rotation = qYaw * qPitch;

        transform.Position = focus - transform.Forward * orbitDistance;
    }

    public void SetAspect(float panelAspect) {
        if (panelAspect > 0f)
            aspect = panelAspect;
    }

    public Matrix4 GetViewMatrix() =>
        BMatrix.LookAt(transform.Position, transform.Position + transform.Forward, transform.Up);

    public Matrix4 GetProjectionMatrix() =>
        BMatrix.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(fovDegrees), aspect, nearPlane, farPlane);

    bool flying;

    public void Update(float dt, bool hovered, EditorInput input) {
        flying = input.RightMouseDown && (flying || hovered);
        if (!flying)
            return;

        if (input.ScrollY > 0) moveSpeed += 1f;
        else if (input.ScrollY < 0) moveSpeed = Math.Max(1f, moveSpeed - 1f);

        yaw -= input.MouseDelta.X * sensitivity;
        pitch += input.MouseDelta.Y * sensitivity;
        pitch = MathHelper.Clamp(pitch, -89f, 89f);

        Quaternion qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(pitch));
        Quaternion qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(yaw));
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

    public string SerializePose() {
        Vector3 p = transform.Position;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0},{1},{2},{3},{4}", p.X, p.Y, p.Z, pitch, yaw);
    }

    public void RestorePose(string pose) {
        if (string.IsNullOrEmpty(pose))
            return;
        var parts = pose.Split(',');
        if (parts.Length != 5)
            return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out float px) ||
            !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out float py) ||
            !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out float pz) ||
            !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out float pi) ||
            !float.TryParse(parts[4], System.Globalization.NumberStyles.Float, ci, out float yw))
            return;

        pitch = MathHelper.Clamp(pi, -89f, 89f);
        yaw = yw;
        Quaternion qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(pitch));
        Quaternion qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(yaw));
        transform.Rotation = qYaw * qPitch;
        transform.Position = new Vector3(px, py, pz);
    }
}
