using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal sealed class ViewportResolution {
    public static readonly (string label, int w, int h)[] Presets = [
        ("Free Aspect", 0, 0),
        ("1920 x 1080", 1920, 1080),
        ("2560 x 1440", 2560, 1440),
        ("1280 x 720", 1280, 720),
        ("1080 x 1920", 1080, 1920), ("800 x 600", 800, 600),
        ("3840 x 2160", 3840, 2160),
    ];

    public static readonly string[] PresetLabels =
        [.. System.Array.ConvertAll(Presets, p => p.label), "Custom...", "Custom Aspect..."];
    public static int CustomIndex => Presets.Length;
    public static int CustomAspectIndex => Presets.Length + 1;

    public int PresetIndex;
    public float Zoom = 1f;
    public int CustomW = 1280, CustomH = 720;
    public int AspectW = 21, AspectH = 9;

    public bool IsCustom => PresetIndex == CustomIndex;
    public bool IsCustomAspect => PresetIndex == CustomAspectIndex;
    public bool IsFree => !IsCustom && !IsCustomAspect && Presets[PresetIndex].w == 0;

    (int w, int h) Fixed => IsCustom
        ? (System.Math.Max(1, CustomW), System.Math.Max(1, CustomH))
        : (Presets[PresetIndex].w, Presets[PresetIndex].h);

    float TargetAspect => IsCustomAspect
        ? (float)System.Math.Max(1, AspectW) / System.Math.Max(1, AspectH)
        : IsFree ? 0f : (float)Fixed.w / Fixed.h;

    public SysVec2 RenderSize(SysVec2 panel) {
        if (IsFree) return panel;
        if (IsCustomAspect) {
            var (size, _) = DisplayRect(panel);
            return size;
        }
        (int w, int h) = Fixed;
        return new SysVec2(w, h);
    }

    public (SysVec2 uv0, SysVec2 uv1) ZoomUVs(bool flipV) {
        float z = System.Math.Max(1f, Zoom);
        float half = 0.5f / z;
        float lo = 0.5f - half, hi = 0.5f + half;
        return flipV
            ? (new SysVec2(lo, hi), new SysVec2(hi, lo))
            : (new SysVec2(lo, lo), new SysVec2(hi, hi));
    }

    public (SysVec2 size, SysVec2 offset) DisplayRect(SysVec2 panel) {
        if (IsFree)
            return (panel, SysVec2.Zero);

        float targetAspect = TargetAspect;
        float panelAspect = panel.X / panel.Y;

        SysVec2 size = panelAspect > targetAspect
            ? new SysVec2(panel.Y * targetAspect, panel.Y)
            : new SysVec2(panel.X, panel.X / targetAspect);

        SysVec2 offset = (panel - size) * 0.5f;
        return (size, offset);
    }
}
