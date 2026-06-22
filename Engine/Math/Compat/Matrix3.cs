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
