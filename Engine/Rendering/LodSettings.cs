namespace BallisticEngine;

// Global geometric-LOD controls the DX12 renderer reads per frame (mirrors how PostFX feeds the renderer).
// Disabled by default → the renderer always selects LOD0 → byte-identical to a no-LOD build. The backend's
// env-door (BALLISTIC_DX12_LOD) flips Enabled; deterministic capture forces it back off via FreezeForDeterminism.
public static class LodSettings {
    // Master enable. When false, every submesh draws LOD0 (CPU + GPU paths short-circuit before any LOD math).
    public static bool Enabled;

    // Forced off during deterministic capture / paused diff so frame 60 == frame 240 and goldens stay bit-exact.
    public static bool FreezeForDeterminism;

    // Pixel-span thresholds (against the render target's pixel dims): LOD k is chosen when the AABB's projected
    // max screen span < SpanThresholds[k-1]. Descending. Defaults tuned for 1080p; scaled by GlobalBias + the
    // per-renderer LodBias.
    public static readonly float[] SpanThresholds = { 300f, 120f, 45f, 15f };

    // Multiplies the measured span before comparison (Unity's LODBias). >1 keeps higher detail longer.
    public static float GlobalBias = 1f;

    // Force every submesh to this LOD (>=0) for A/B captures of a single level; -1 = normal selection.
    public static int ForceLod = -1;

    public static bool Active => Enabled && !FreezeForDeterminism;
}
