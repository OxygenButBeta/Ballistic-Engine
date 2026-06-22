namespace BallisticEngine.UI;

public enum Easing
{
    Linear,
    EaseOut,
    EaseIn,
    EaseInOut,
}

public static class EasingFunctions
{
    public static float Apply(Easing e, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return e switch
        {
            Easing.EaseOut => 1f - MathF.Pow(1f - t, 3f),
            Easing.EaseIn => t * t * t,
            Easing.EaseInOut => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,
            _ => t,
        };
    }
}
