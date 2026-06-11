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

    public static readonly string[] PresetLabels = System.Array.ConvertAll(Presets, p => p.label);

    public int PresetIndex;          // index into Presets
    public float Zoom = 1f;          // 1 .. 8  image magnification (zoom INTO the rendered picture)

    public bool IsFree => Presets[PresetIndex].w == 0;

    // The pixel resolution to render at, given the available panel size. Zoom does NOT change this —
    // it magnifies the displayed image (samples a smaller centered region), exactly like zooming into
    // a photo: the render stays the same, you just look closer at part of it.
    public SysVec2 RenderSize(SysVec2 panel) =>
        IsFree ? panel : new SysVec2(Presets[PresetIndex].w, Presets[PresetIndex].h);

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

        float targetAspect = (float)Presets[PresetIndex].w / Presets[PresetIndex].h;
        float panelAspect = panel.X / panel.Y;

        SysVec2 size = panelAspect > targetAspect
            ? new SysVec2(panel.Y * targetAspect, panel.Y)   // panel wider: fit height
            : new SysVec2(panel.X, panel.X / targetAspect);  // panel taller: fit width

        SysVec2 offset = (panel - size) * 0.5f;
        return (size, offset);
    }
}
