using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Phase E (RW2) — the single source of truth for editor TYPOGRAPHY + DRAWER-ROW visual style.
//
// Before this existed every panel hand-rolled PushStyleColor and the inspector used ONE font size for
// everything (headers were "bold" only by weight, never by SIZE), so the UI read flat — the "çiğ/standart
// görünüyor" the user flagged. EditorTheme centralizes:
//   - a SEMANTIC TYPE SCALE: Display / Header / Body / Caption font handles (real distinct pixel sizes,
//     baked into the atlas by ImGuiController.LoadFont and assigned here on every atlas rebuild), so a
//     header reads as a header and a caption recedes. This is the #1 flatness fix (plan §5 E2).
//   - the drawer-ROW palette + metrics (label color, hover-accent bar, icon-affordance gutter) consumed by
//     InspectorPanel.Row / RowWithTooltip so EVERY member row (component members + all shim rows) picks up
//     the same look in one place (plan §5 E3) — no per-panel widget hand-rolling (attribute-driven mandate).
//
// PERFORMANCE (plan §4, first-class constraint): the font handles are resolved ONCE per atlas build (NOT
// per frame). Row decoration is a single AddRectFilled on the HOVERED row only (budgeted DrawList, no
// per-row gradient/shadow). Colors are precomputed constants. Zero per-frame reflection / allocation.
internal static class EditorTheme {
    // --- Semantic type scale (assigned by ImGuiController on every atlas (re)build) -------------------
    // All default to the default font so the editor is never font-less before LoadFont runs / if the
    // .ttf is missing — callers can PushFont unconditionally.
    public static ImFontPtr Display { get; internal set; }   // big numbers / empty-state titles
    public static ImFontPtr Header  { get; internal set; }    // component / section headers (semibold, larger)
    public static ImFontPtr Body    { get; internal set; }    // default UI text (== the base font)
    public static ImFontPtr Caption { get; internal set; }    // secondary/disabled hints, smaller

    // Base body size in (DPI-scaled) px, set by LoadFont so callers can derive metrics; the semantic sizes
    // are multiples of it (see ImGuiController.LoadFont). 16.5 matches the historical single size.
    public static float BodySize { get; internal set; } = 16.5f;

    // Semantic size multipliers off BodySize (one place to retune the scale).
    public const float DisplayScale = 1.62f;
    public const float HeaderScale  = 1.12f;
    public const float CaptionScale = 0.84f;

    // --- Drawer-row palette ---------------------------------------------------------------------------
    // The member LABEL was drawn with TextDisabled (the dead grey that made rows read as "off"). A real,
    // legible label color + a recessive caption color give the rows a proper hierarchy. Tuned to the
    // graphite palette in ImGuiController.ApplyColors.
    public static readonly SysVec4 RowLabel   = new(0.80f, 0.83f, 0.88f, 1f);   // brighter than TextDisabled
    public static readonly SysVec4 RowCaption = new(0.52f, 0.56f, 0.63f, 1f);   // the "(?)" badge / hints

    // Hover-accent bar drawn at the LEFT edge of a hovered row (the affordance the flat rows lacked). Faint
    // fill across the row + a brighter accent sliver — one AddRectFilled each, hover-gated (cheap).
    public static SysVec4 RowHoverFill(SysVec4 accent) => new(accent.X, accent.Y, accent.Z, 0.055f);
    public static SysVec4 RowHoverBar(SysVec4 accent)  => new(accent.X, accent.Y, accent.Z, 0.85f);

    public const float RowAccentBarWidth = 2.5f;   // left sliver width (px, pre-scale — small on purpose)

    // Convenience: PushFont(Header) for the duration of a using-less call site is awkward, so callers do
    // ImGui.PushFont(EditorTheme.Header); ...; ImGui.PopFont() directly. No wrapper needed.
}
