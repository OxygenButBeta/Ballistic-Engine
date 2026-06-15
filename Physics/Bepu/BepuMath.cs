namespace BallisticEngine.Bepu;

// Engine <-> BepuPhysics math conversions. Both now speak System.Numerics (the engine migrated off
// OpenTK.Mathematics in ENDGAME 3 step 4, and BepuPhysics was always System.Numerics), so these are
// identity passthroughs — kept so the rest of the backend's call sites read unchanged (and so a future
// type split, if any, has one place to live). Distinct names (ToNumerics/ToOpenTK) over the same
// signature are legal; overload resolution is by name.
static class BepuMath {
    public static Vector3 ToNumerics(in Vector3 v) => v;
    public static Vector3 ToOpenTK(in Vector3 v) => v;
    public static Quaternion ToNumerics(in Quaternion q) => q;
    public static Quaternion ToOpenTK(in Quaternion q) => q;
}
