
namespace BallisticEngine;

public static class Mathf {
    public const float PI = MathF.PI;
    public const float Tau = MathF.Tau;
    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;
    public const float Epsilon = 1e-6f;
    public const float Infinity = float.PositiveInfinity;
    public const float NegativeInfinity = float.NegativeInfinity;

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

    public static float Min(float a, float b) => a < b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;
    public static int Min(int a, int b) => a < b ? a : b;
    public static int Max(int a, int b) => a > b ? a : b;

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;

    public static float LerpAngle(float a, float b, float t) {
        float delta = Repeat(b - a, 360f);
        if (delta > 180f)
            delta -= 360f;
        return a + delta * Clamp01(t);
    }

    public static float InverseLerp(float a, float b, float value) =>
        a == b ? 0f : Clamp01((value - a) / (b - a));

    public static float SmoothStep(float from, float to, float t) {
        t = Clamp01((t - from) / (to - from == 0f ? 1f : to - from));
        return t * t * (3f - 2f * t);
    }

    public static float MoveTowards(float current, float target, float maxDelta) {
        if (MathF.Abs(target - current) <= maxDelta)
            return target;
        return current + Sign(target - current) * maxDelta;
    }

    public static float MoveTowardsAngle(float current, float target, float maxDelta) {
        float delta = DeltaAngle(current, target);
        if (-maxDelta < delta && delta < maxDelta)
            return target;
        return MoveTowards(current, current + delta, maxDelta);
    }

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

        if (originalTo - current > 0f == output > originalTo) {
            output = originalTo;
            velocity = (output - originalTo) / deltaTime;
        }
        return output;
    }

    public static float Repeat(float t, float length) =>
        Clamp(t - MathF.Floor(t / length) * length, 0f, length);

    public static float PingPong(float t, float length) {
        t = Repeat(t, length * 2f);
        return length - MathF.Abs(t - length);
    }

    public static float DeltaAngle(float current, float target) {
        float delta = Repeat(target - current, 360f);
        if (delta > 180f)
            delta -= 360f;
        return delta;
    }

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, Clamp01(t));
    public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta) {
        Vector3 delta = target - current;
        float dist = delta.Length();
        if (dist <= maxDistanceDelta || dist < Epsilon)
            return target;
        return current + delta / dist * maxDistanceDelta;
    }

    public static Quaternion Slerp(Quaternion a, Quaternion b, float t) =>
        Quaternion.Slerp(a, b, Clamp01(t));

    public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta) {
        float angle = AngleBetween(from, to);
        if (angle < Epsilon)
            return to;
        return Quaternion.Slerp(from, to, Min(1f, maxDegreesDelta / angle));
    }

    public static float AngleBetween(Quaternion a, Quaternion b) {
        float dot = MathF.Abs(Clamp(QuatDot(a, b), -1f, 1f));
        return dot > 0.999999f ? 0f : 2f * MathF.Acos(dot) * Rad2Deg;
    }

    static float QuatDot(Quaternion a, Quaternion b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    public static bool Approximately(float a, float b) =>
        MathF.Abs(b - a) < Max(1e-6f * Max(MathF.Abs(a), MathF.Abs(b)), Epsilon * 8f);
}
