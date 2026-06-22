public static class MathHelper {
    public const float Pi = MathF.PI;
    public const float TwoPi = 2f * MathF.PI;
    public const float PiOver2 = MathF.PI / 2f;
    public const float PiOver3 = MathF.PI / 3f;
    public const float PiOver4 = MathF.PI / 4f;
    public const float PiOver6 = MathF.PI / 6f;
    public const float ThreePiOver2 = 3f * MathF.PI / 2f;

    public static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
    public static float RadiansToDegrees(float radians) => radians * (180f / MathF.PI);
    public static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);
    public static double RadiansToDegrees(double radians) => radians * (180.0 / Math.PI);

    public static float Clamp(float n, float min, float max) => n < min ? min : n > max ? max : n;
    public static int Clamp(int n, int min, int max) => n < min ? min : n > max ? max : n;
    public static double Clamp(double n, double min, double max) => n < min ? min : n > max ? max : n;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

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

public static class BQuaternion {

    public static Quaternion FromEulerAngles(Vector3 eulerRadians) =>
        FromEulerAngles(eulerRadians.X, eulerRadians.Y, eulerRadians.Z);

    public static Quaternion FromEulerAngles(float x, float y, float z) {
        float c1 = MathF.Cos(x * 0.5f), s1 = MathF.Sin(x * 0.5f);
        float c2 = MathF.Cos(y * 0.5f), s2 = MathF.Sin(y * 0.5f);
        float c3 = MathF.Cos(z * 0.5f), s3 = MathF.Sin(z * 0.5f);
        return new Quaternion(
            (s1 * c2 * c3) + (c1 * s2 * s3),
            (c1 * s2 * c3) - (s1 * c2 * s3),
            (c1 * c2 * s3) + (s1 * s2 * c3),
            (c1 * c2 * c3) - (s1 * s2 * s3));
    }
}

public struct Vector2i {
    public int X, Y;
    public Vector2i(int x, int y) { X = x; Y = y; }
}

public struct Vector3i {
    public int X, Y, Z;
    public Vector3i(int x, int y, int z) { X = x; Y = y; Z = z; }
}

public struct Vector4i {
    public int X, Y, Z, W;
    public Vector4i(int x, int y, int z, int w) { X = x; Y = y; Z = z; W = w; }
}

public struct Matrix3 {
    public float M11, M12, M13;
    public float M21, M22, M23;
    public float M31, M32, M33;

    public Matrix3(Matrix4x4 m) {
        M11 = m.M11; M12 = m.M12; M13 = m.M13;
        M21 = m.M21; M22 = m.M22; M23 = m.M23;
        M31 = m.M31; M32 = m.M32; M33 = m.M33;
    }

    public float Determinant =>
        (M11 * (M22 * M33 - M23 * M32)) -
        (M12 * (M21 * M33 - M23 * M31)) +
        (M13 * (M21 * M32 - M22 * M31));

    public static Matrix3 Transpose(Matrix3 m) => new() {
        M11 = m.M11, M12 = m.M21, M13 = m.M31,
        M21 = m.M12, M22 = m.M22, M23 = m.M32,
        M31 = m.M13, M32 = m.M23, M33 = m.M33,
    };

    public static Matrix3 Invert(Matrix3 m) {
        float det = m.Determinant;
        if (MathF.Abs(det) < 1e-24f)
            return m;
        float inv = 1f / det;
        return new Matrix3 {
            M11 = (m.M22 * m.M33 - m.M23 * m.M32) * inv,
            M12 = (m.M13 * m.M32 - m.M12 * m.M33) * inv,
            M13 = (m.M12 * m.M23 - m.M13 * m.M22) * inv,
            M21 = (m.M23 * m.M31 - m.M21 * m.M33) * inv,
            M22 = (m.M11 * m.M33 - m.M13 * m.M31) * inv,
            M23 = (m.M13 * m.M21 - m.M11 * m.M23) * inv,
            M31 = (m.M21 * m.M32 - m.M22 * m.M31) * inv,
            M32 = (m.M12 * m.M31 - m.M11 * m.M32) * inv,
            M33 = (m.M11 * m.M22 - m.M12 * m.M21) * inv,
        };
    }
}

public static class OpenTkCompatExtensions {
    public static Vector2 Normalized(this Vector2 v) => Vector2.Normalize(v);
    public static Vector3 Normalized(this Vector3 v) => Vector3.Normalize(v);
    public static Vector4 Normalized(this Vector4 v) => Vector4.Normalize(v);

    public static Vector3 Xyz(this Vector4 v) => new(v.X, v.Y, v.Z);
    public static Vector2 Xy(this Vector3 v) => new(v.X, v.Y);

    public static Matrix4x4 Inverted(this Matrix4x4 m) =>
        Matrix4x4.Invert(m, out Matrix4x4 r) ? r : m;

    public static Vector3 ExtractTranslation(this Matrix4x4 m) => m.Translation;

    public static Vector3 ExtractScale(this Matrix4x4 m) =>
        Matrix4x4.Decompose(m, out Vector3 scale, out _, out _)
            ? scale
            : new Vector3(
                new Vector3(m.M11, m.M12, m.M13).Length(),
                new Vector3(m.M21, m.M22, m.M23).Length(),
                new Vector3(m.M31, m.M32, m.M33).Length());

    public static Quaternion ExtractRotation(this Matrix4x4 m) =>
        Matrix4x4.Decompose(m, out _, out Quaternion rot, out _) ? rot : Quaternion.Identity;

    public static Vector3 ToEulerAngles(this Quaternion q) {
        float sinrCosp = 2f * (q.W * q.X + q.Y * q.Z);
        float cosrCosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float x = MathF.Atan2(sinrCosp, cosrCosp);
        float sinp = 2f * (q.W * q.Y - q.Z * q.X);
        float y = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);
        float sinyCosp = 2f * (q.W * q.Z + q.X * q.Y);
        float cosyCosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        float z = MathF.Atan2(sinyCosp, cosyCosp);
        return new Vector3(x, y, z);
    }
}
