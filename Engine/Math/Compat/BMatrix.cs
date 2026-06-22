public static class BMatrix {
    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up) {
        Vector3 z = Vector3.Normalize(eye - target);
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, z));
        Vector3 y = Vector3.Normalize(Vector3.Cross(z, x));
        return new Matrix4x4(
            x.X, y.X, z.X, 0f,
            x.Y, y.Y, z.Y, 0f,
            x.Z, y.Z, z.Z, 0f,
            -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1f);
    }

    public static Matrix4x4 CreatePerspectiveFieldOfView(float fovy, float aspect, float near, float far) {
        float f = 1f / MathF.Tan(fovy * 0.5f);
        var m = new Matrix4x4();
        m.M11 = f / aspect;
        m.M22 = f;
        m.M33 = (far + near) / (near - far);
        m.M34 = -1f;
        m.M43 = (2f * far * near) / (near - far);
        return m;
    }

    public static Matrix4x4 CreateOrthographic(float width, float height, float near, float far) {
        var m = Matrix4x4.Identity;
        m.M11 = 2f / width;
        m.M22 = 2f / height;
        m.M33 = -2f / (far - near);
        m.M43 = -(far + near) / (far - near);
        return m;
    }
}
