namespace BallisticEngine;

public static class LodSettings {
    public static bool Enabled;

    public static bool FreezeForDeterminism;

    public static readonly float[] SpanThresholds = { 300f, 120f, 45f, 15f };

    public static float GlobalBias = 1f;

    public static int ForceLod = -1;

    public static bool Active => Enabled && !FreezeForDeterminism;
}
