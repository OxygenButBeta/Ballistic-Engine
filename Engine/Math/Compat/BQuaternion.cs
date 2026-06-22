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
