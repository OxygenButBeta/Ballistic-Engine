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
    public static readonly SysVec4 RowLabel   = new(0.78f, 0.81f, 0.86f, 1f);   // brighter than TextDisabled (>=9:1 on inputs)
    public static readonly SysVec4 RowCaption = new(0.55f, 0.58f, 0.65f, 1f);   // the "(?)" badge / hints

    // Hover-accent bar drawn at the LEFT edge of a hovered row (the affordance the flat rows lacked). Faint
    // fill across the row + a brighter accent sliver — one AddRectFilled each, hover-gated (cheap).
    public static SysVec4 RowHoverFill(SysVec4 accent) => new(accent.X, accent.Y, accent.Z, 0.055f);
    public static SysVec4 RowHoverBar(SysVec4 accent)  => new(accent.X, accent.Y, accent.Z, 0.85f);

    public const float RowAccentBarWidth = 2.5f;   // left sliver width (px, pre-scale — small on purpose)

    // --- Surface PALETTE (RW3) -------------------------------------------------------------------------
    // Single source for the graphite elevation ramp that ImGuiController.ApplyColors hand-derives as LOCALS.
    // RW3's in-viewport toolbar overlay (and the eventual E1 centralized theme) read these so the chrome
    // matches the panels WITHOUT re-typing hex constants in two places — the exact "koddan elle tanımlama"
    // duplication the rework fights. Values mirror ApplyColors' bg0..titleBg ramp byte-for-byte; if that ramp
    // is retuned, retune here too (a future E1 step folds ApplyColors onto these so they can't drift).
    // EF5a — mirrors ImGuiController.ApplyColors' deep-graphite UE5 ramp byte-for-byte (see note above).
    public static readonly SysVec4 Bg0         = Rgb(0x16181C);   // window background — base graphite
    public static readonly SysVec4 Bg1         = Rgb(0x1D2026);   // child / popup — raised surface
    public static readonly SysVec4 Bg2         = Rgb(0x262A31);   // frames (inputs)
    public static readonly SysVec4 Bg3         = Rgb(0x333842);   // hovered frames
    public static readonly SysVec4 HeaderBg    = Rgb(0x2B3038);   // collapsing headers / selected tabs
    public static readonly SysVec4 Text        = Rgb(0xECEEF2);   // bright primary text
    public static readonly SysVec4 TextDim     = Rgb(0x8C94A1);   // disabled / secondary text
    public static readonly SysVec4 Border      = Rgb(0x0C0E11);   // seam where one is still wanted
    public static readonly SysVec4 BorderLight = Rgb(0x363C46);   // subtle inner dividers
    public static readonly SysVec4 TitleBg     = Rgb(0x121418);   // title bars

    // In-viewport toolbar chrome (RW3 E7). The overlay floats OVER the 3D image, so it needs its own
    // translucent surface + pill so the controls read against any scene. Tuned off the ramp above.
    public static readonly SysVec4 OverlayBg   = new(0.086f, 0.094f, 0.110f, 0.82f);  // ~Bg0 @ 0.82 — pill backing
    public static readonly SysVec4 OverlayPill = new(0.0f, 0.0f, 0.0f, 0.30f);        // segmented-control backing
    public static readonly SysVec4 OverlayBorder = new(1f, 1f, 1f, 0.07f);            // hairline around the pill
    public const float OverlayRounding = 7f;   // pill corner radius (px, pre-scale)
    public const float OverlayMargin   = 10f;  // gap from the viewport edges (px, pre-scale)

    // Convenience: PushFont(Header) for the duration of a using-less call site is awkward, so callers do
    // ImGui.PushFont(EditorTheme.Header); ...; ImGui.PopFont() directly. No wrapper needed.

    // Local hex helper so the palette can be authored as 0xRRGGBB without depending on ImGuiController.
    static SysVec4 Rgb(int hex) => new(
        ((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, 1f);
}
