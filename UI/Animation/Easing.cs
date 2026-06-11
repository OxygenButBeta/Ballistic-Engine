using System;

namespace BallisticEngine.UI;

// Easing curves for UI transitions. The Black Hollow design specifies cubic-bezier(0.2,0.7,0.3,1)
// for selection motion; we approximate the common CSS curves with closed forms (good enough for UI,
// no per-frame bezier solve). t is 0..1, returns the eased 0..1.
public enum Easing
{
    Linear,
    EaseOut,      // decelerate — the default "feels responsive" curve (≈ cubic-bezier(0.2,0.7,0.3,1))
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
            Easing.EaseOut => 1f - MathF.Pow(1f - t, 3f),        // cubic ease-out
            Easing.EaseIn => t * t * t,                           // cubic ease-in
            Easing.EaseInOut => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,
            _ => t,
        };
    }
}
