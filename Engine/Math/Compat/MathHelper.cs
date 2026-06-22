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
