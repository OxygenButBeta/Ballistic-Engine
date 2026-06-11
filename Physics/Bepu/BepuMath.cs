namespace BallisticEngine.Bepu;

// OpenTK <-> System.Numerics conversions. The engine speaks OpenTK.Mathematics everywhere;
// BepuPhysics speaks System.Numerics. Keep ALL conversions here so the rest of the backend
// reads cleanly.
static class BepuMath {
    public static System.Numerics.Vector3 ToNumerics(in OpenTK.Mathematics.Vector3 v) =>
        new(v.X, v.Y, v.Z);

    public static OpenTK.Mathematics.Vector3 ToOpenTK(in System.Numerics.Vector3 v) =>
        new(v.X, v.Y, v.Z);

    public static System.Numerics.Quaternion ToNumerics(in OpenTK.Mathematics.Quaternion q) =>
        new(q.X, q.Y, q.Z, q.W);

    public static OpenTK.Mathematics.Quaternion ToOpenTK(in System.Numerics.Quaternion q) =>
        new(q.X, q.Y, q.Z, q.W);
}
