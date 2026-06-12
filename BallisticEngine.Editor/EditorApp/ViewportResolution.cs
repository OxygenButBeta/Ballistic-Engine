using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Per-view (Scene / Game) resolution control: a preset (Free Aspect = match the panel, or a fixed
// resolution like 1920x1080) plus a render-scale multiplier (Unity's "Scale" slider — supersample
// above 1.0, downsample below for performance). The panel always DISPLAYS fit-to-area; only the
// offscreen render target's pixel size changes. RenderSize() returns the resolution to render at;
// DisplaySize() returns the on-screen rectangle (letterboxed to preserve a fixed aspect).
internal sealed class ViewportResolution {
    // (label, width, height). 0x0 = Free Aspect (track the panel).
    public static readonly (string label, int w, int h)[] Presets = [
        ("Free Aspect", 0, 0),
        ("1920 x 1080", 1920, 1080),
        ("2560 x 1440", 2560, 1440),
        ("1280 x 720", 1280, 720),
        ("1080 x 1920", 1080, 1920),   // portrait
        ("800 x 600", 800, 600),
        ("3840 x 2160", 3840, 2160),
    ];

    // The combo lists every preset plus two trailing custom entries: "Custom..." (exact W x H) and
    // "Custom Aspect..." (a fixed RATIO that fills the panel, like 21:9). CustomIndex is one past the
    // last preset; CustomAspectIndex follows it.
    public static readonly string[] PresetLabels =
        [.. System.Array.ConvertAll(Presets, p => p.label), "Custom...", "Custom Aspect..."];
    public static int CustomIndex => Presets.Length;
    public static int CustomAspectIndex => Presets.Length + 1;

    public int PresetIndex;          // index into PresetLabels
    public float Zoom = 1f;          // 1 .. 8  image magnification (zoom INTO the rendered picture)
    public int CustomW = 1280, CustomH = 720;
    public int AspectW = 21, AspectH = 9;   // for Custom Aspect mode

    public bool IsCustom => PresetIndex == CustomIndex;
    public bool IsCustomAspect => PresetIndex == CustomAspectIndex;
    public bool IsFree => !IsCustom && !IsCustomAspect && Presets[PresetIndex].w == 0;

    // The fixed resolution for the current selection (exact-custom or preset). Only valid when the
    // selection is a fixed RESOLUTION (not Free, not Custom Aspect).
    (int w, int h) Fixed => IsCustom
        ? (System.Math.Max(1, CustomW), System.Math.Max(1, CustomH))
        : (Presets[PresetIndex].w, Presets[PresetIndex].h);

    // The target aspect (width/height) for the current selection, or 0 if Free.
    float TargetAspect => IsCustomAspect
        ? (float)System.Math.Max(1, AspectW) / System.Math.Max(1, AspectH)
        : IsFree ? 0f : (float)Fixed.w / Fixed.h;

    // The pixel resolution to render at, given the available panel size. Zoom does NOT change this —
    // it magnifies the displayed image (samples a smaller centered region), exactly like zooming into
    // a photo: the render stays the same, you just look closer at part of it.
    public SysVec2 RenderSize(SysVec2 panel) {
        if (IsFree) return panel;
        // Custom Aspect renders at the largest rect of that ratio that fits the panel (no fixed pixel
        // count — it tracks the panel like Free, just letterboxed to the ratio).
        if (IsCustomAspect) {
            var (size, _) = DisplayRect(panel);
            return size;
        }
        (int w, int h) = Fixed;
        return new SysVec2(w, h);
    }

    // UV rectangle (uv0, uv1) for the displayed image, accounting for zoom. At zoom 1 it's the full
    // [0,1] (V flipped, since the GL texture is bottom-up). Higher zoom samples a centered sub-region
    // — that's the "look closer" magnification. Returns flipped-V UVs ready for ImGui.Image.
    public (SysVec2 uv0, SysVec2 uv1) ZoomUVs() {
        float z = System.Math.Max(1f, Zoom);
        float half = 0.5f / z;                 // half-extent of the sampled region around center (0.5)
        float lo = 0.5f - half, hi = 0.5f + half;
        // V is flipped (top-left origin for ImGui vs bottom-left for GL): uv0.Y = hi, uv1.Y = lo.
        return (new SysVec2(lo, hi), new SysVec2(hi, lo));
    }

    // The on-screen rectangle: for Free Aspect it fills the panel; for a fixed resolution it's the
    // largest rect of that aspect that fits the panel (letterboxed), so the image isn't distorted.
    public (SysVec2 size, SysVec2 offset) DisplayRect(SysVec2 panel) {
        if (IsFree)
            return (panel, SysVec2.Zero);

        float targetAspect = TargetAspect;
        float panelAspect = panel.X / panel.Y;

        SysVec2 size = panelAspect > targetAspect
            ? new SysVec2(panel.Y * targetAspect, panel.Y)   // panel wider: fit height
            : new SysVec2(panel.X, panel.X / targetAspect);  // panel taller: fit width

        SysVec2 offset = (panel - size) * 0.5f;
        return (size, offset);
    }
}
