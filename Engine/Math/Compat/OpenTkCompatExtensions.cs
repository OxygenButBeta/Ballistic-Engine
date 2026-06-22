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
