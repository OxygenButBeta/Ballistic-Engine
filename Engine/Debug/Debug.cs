
namespace BallisticEngine;

public static class Debug {
    public static void Log(object message, BObject context = null) => Debugging.Log(message, context);
    public static void LogWarning(object message, BObject context = null) => Debugging.LogWarning(message, context);
    public static void LogError(object message, BObject context = null) => Debugging.LogError(message, context);

    public static void Assert(bool condition, object message = null) {
        if (!condition)
            Debugging.LogError(message is null ? "Assertion failed." : $"Assertion failed: {message}");
    }

    public static void DrawLine(Vector3 start, Vector3 end) => DebugDraw.DrawLine(start, end);
    public static void DrawLine(Vector3 start, Vector3 end, Vector3 color, float duration = 0f) =>
        DebugDraw.DrawLine(start, end, color, duration);

    public static void DrawRay(Vector3 origin, Vector3 direction) => DebugDraw.DrawRay(origin, direction);
    public static void DrawRay(Vector3 origin, Vector3 direction, Vector3 color, float duration = 0f) =>
        DebugDraw.DrawRay(origin, direction, color, duration);

    public static void DrawWireSphere(Vector3 center, float radius, Vector3 color, float duration = 0f) =>
        DebugDraw.DrawWireSphere(center, radius, color, duration);

    public static void DrawWireCube(Vector3 center, Vector3 size, Vector3 color, float duration = 0f) =>
        DebugDraw.DrawWireCube(center, size, color, Quaternion.Identity, duration);
}
