using OpenTK.Mathematics;

namespace BallisticEngine;

// Single-precision math helpers (Unity's `Mathf`). The engine math is float-first (OpenTK's
// Vector3/Quaternion are float), so game code shouldn't have to reach for System.Math (double)
// and cast everywhere. Pure BCL/OpenTK — lives in the Engine layer, callable from game scripts.
//
// Everything here mirrors Unity semantics so ported gameplay code behaves identically: Lerp is
// clamped, LerpUnclamped is not, angles are in DEGREES (Sin/Cos take radians — those match MathF),
// and SmoothDamp uses the same critically-damped spring as Unity's.
public static class Mathf {
    public const float PI = MathF.PI;
    public const float Tau = MathF.Tau;
    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;
    public const float Epsilon = 1e-6f;
    public const float Infinity = float.PositiveInfinity;
    public const float NegativeInfinity = float.NegativeInfinity;

    // ---- Trig / roots (thin MathF passthroughs so game code has one math entry point) ------
    public static float Sin(float x) => MathF.Sin(x);
    public static float Cos(float x) => MathF.Cos(x);
    public static float Tan(float x) => MathF.Tan(x);
    public static float Asin(float x) => MathF.Asin(x);
    public static float Acos(float x) => MathF.Acos(x);
    public static float Atan(float x) => MathF.Atan(x);
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
    public static float Sqrt(float x) => MathF.Sqrt(x);
    public static float Pow(float x, float p) => MathF.Pow(x, p);
    public static float Exp(float x) => MathF.Exp(x);
    public static float Log(float x) => MathF.Log(x);
    public static float Log10(float x) => MathF.Log10(x);

    public static float Abs(float x) => MathF.Abs(x);
    public static int Abs(int x) => Math.Abs(x);
    public static float Sign(float x) => x >= 0f ? 1f : -1f;
    public static float Floor(float x) => MathF.Floor(x);
    public static float Ceil(float x) => MathF.Ceiling(x);
    public static float Round(float x) => MathF.Round(x);
    public static int FloorToInt(float x) => (int)MathF.Floor(x);
    public static int CeilToInt(float x) => (int)MathF.Ceiling(x);
    public static int RoundToInt(float x) => (int)MathF.Round(x);

    // ---- Min / max / clamp -----------------------------------------------------------------
    public static float Min(float a, float b) => a < b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;
    public static int Min(int a, int b) => a < b ? a : b;
    public static int Max(int a, int b) => a > b ? a : b;

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    // [0,1] clamp — the common normalized-parameter case.
    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    // ---- Interpolation ---------------------------------------------------------------------

    // Clamped linear interpolation (Unity's Lerp): t is clamped to [0,1] first.
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    // Unclamped — t may extrapolate past the endpoints.
    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;

    // Shortest-path angular lerp in DEGREES (wraps across the 360 seam).
    public static float LerpAngle(float a, float b, float t) {
        float delta = Repeat(b - a, 360f);
        if (delta > 180f)
            delta -= 360f;
        return a + delta * Clamp01(t);
    }

    // Inverse of Lerp: the t that produces `value` between a and b (clamped to [0,1]).
    public static float InverseLerp(float a, float b, float value) =>
        a == b ? 0f : Clamp01((value - a) / (b - a));

    // Hermite smoothstep (ease in/out) across [from,to].
    public static float SmoothStep(float from, float to, float t) {
        t = Clamp01((t - from) / (to - from == 0f ? 1f : to - from));
        return t * t * (3f - 2f * t);
    }

    // ---- Movement helpers ------------------------------------------------------------------

    // Moves `current` toward `target` by at most `maxDelta` (never overshoots). The workhorse
    // for frame-rate-independent approach (call with speed * Time.DeltaTime each frame).
    public static float MoveTowards(float current, float target, float maxDelta) {
        if (MathF.Abs(target - current) <= maxDelta)
            return target;
        return current + Sign(target - current) * maxDelta;
    }

    // MoveTowards on an angle in DEGREES (takes the shortest path across the seam).
    public static float MoveTowardsAngle(float current, float target, float maxDelta) {
        float delta = DeltaAngle(current, target);
        if (-maxDelta < delta && delta < maxDelta)
            return target;
        return MoveTowards(current, current + delta, maxDelta);
    }

    // Unity's critically-damped spring smoothing. `velocity` is caller-owned state (pass the
    // same ref every frame). smoothTime ~ how long to roughly reach the target.
    public static float SmoothDamp(float current, float target, ref float velocity,
        float smoothTime, float deltaTime, float maxSpeed = Infinity) {
        smoothTime = Max(0.0001f, smoothTime);
        float omega = 2f / smoothTime;
        float x = omega * deltaTime;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        float change = current - target;
        float originalTo = target;

        float maxChange = maxSpeed * smoothTime;
        change = Clamp(change, -maxChange, maxChange);
        target = current - change;

        float temp = (velocity + omega * change) * deltaTime;
        velocity = (velocity - omega * temp) * exp;
        float output = target + (change + temp) * exp;

        // Prevent overshooting past the target.
        if (originalTo - current > 0f == output > originalTo) {
            output = originalTo;
            velocity = (output - originalTo) / deltaTime;
        }
        return output;
    }

    // ---- Angles ----------------------------------------------------------------------------

    // Loops `t` into [0, length) (Unity's Repeat) — never returns the upper bound.
    public static float Repeat(float t, float length) =>
        Clamp(t - MathF.Floor(t / length) * length, 0f, length);

    // Ping-pongs between 0 and length.
    public static float PingPong(float t, float length) {
        t = Repeat(t, length * 2f);
        return length - MathF.Abs(t - length);
    }

    // Shortest signed difference between two angles in DEGREES, in (-180, 180].
    public static float DeltaAngle(float current, float target) {
        float delta = Repeat(target - current, 360f);
        if (delta > 180f)
            delta -= 360f;
        return delta;
    }

    // ---- Vector helpers (parity with Unity's Vector3.Lerp/MoveTowards static methods that
    //      game code expects on the math facade) -------------------------------------------

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, Clamp01(t));
    public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta) {
        Vector3 delta = target - current;
        float dist = delta.Length;
        if (dist <= maxDistanceDelta || dist < Epsilon)
            return target;
        return current + delta / dist * maxDistanceDelta;
    }

    // Spherical interpolation between two rotations (clamped).
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t) =>
        Quaternion.Slerp(a, b, Clamp01(t));

    // Rotates `from` toward `to` by at most maxDegreesDelta (Unity's RotateTowards).
    public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta) {
        float angle = AngleBetween(from, to);
        if (angle < Epsilon)
            return to;
        return Quaternion.Slerp(from, to, Min(1f, maxDegreesDelta / angle));
    }

    // Unsigned angle in DEGREES between two rotations.
    public static float AngleBetween(Quaternion a, Quaternion b) {
        float dot = MathF.Abs(Clamp(QuatDot(a, b), -1f, 1f));
        return dot > 0.999999f ? 0f : 2f * MathF.Acos(dot) * Rad2Deg;
    }

    // OpenTK's Quaternion has no static Dot; compute it directly (x,y,z,w components).
    static float QuatDot(Quaternion a, Quaternion b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    public static bool Approximately(float a, float b) =>
        MathF.Abs(b - a) < Max(1e-6f * Max(MathF.Abs(a), MathF.Abs(b)), Epsilon * 8f);
}
