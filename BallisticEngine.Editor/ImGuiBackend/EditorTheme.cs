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

    // --- SEMANTIC tokens (EF5b) -----------------------------------------------------------------------
    // The single source for the meaning-carrying colors panels used to hand-type inline (the "bypass
    // offenders" that gave the UI its raw feel). Each is named by ROLE, not by hue, so a panel reads
    // `EditorTheme.Error` not `new SysVec4(1f, 0.5f, 0.4f, 1f)`. Tuned to the deep-graphite UE5 identity
    // (EF5a): saturated enough to read as status against the dark ramp, never neon. Re-tune here once.
    public static readonly SysVec4 Error     = Rgb(0xFF8066);   // invalid input / error text (amber-red)
    public static readonly SysVec4 Warning   = Rgb(0xFFB840);   // disabled-override / caution text (amber)
    public static readonly SysVec4 Success   = Rgb(0x80D980);   // build-succeeded / OK summary (green)
    public static readonly SysVec4 PrefabBlue = Rgb(0x73A8FF);  // prefab-instance accent (Unity's prefab blue)
    public static readonly SysVec4 RowChild  = Rgb(0xB8BDC7);   // hierarchy child label — dimmer than a root's white
    public static readonly SysVec4 IconMuted = new(0.45f, 0.47f, 0.52f, 0.6f);  // inactive ghost-icon (eye toggle)

    // Primary-action button (the green "Create" affordance) as a base color — call sites push the three
    // hover/active variants off it (Btn/BtnHovered/BtnActive) so the whole button stays in one family.
    public static readonly SysVec4 PrimaryAction        = Rgb(0x33A352);   // resting
    public static readonly SysVec4 PrimaryActionHovered = Rgb(0x44C268);
    public static readonly SysVec4 PrimaryActionActive  = Rgb(0x2A8C44);

    // Folder gold — the asset browser's signature folder tint (icon + tree). The full/active variant and
    // the dim/empty variant (lower alpha) used to be two hand-typed literals; derive the dim from this.
    public static readonly SysVec4 FolderTint = Rgb(0xEBC25C);             // full / current / ancestor folder
    public static SysVec4 FolderTintDim => new(0xDB / 255f, 0xB3 / 255f, 0x57 / 255f, 0.75f);  // empty / inactive

    // Log severity ramp (Console). Index by level 0/1/2 — info recedes, warning amber, error red.
    public static readonly SysVec4[] LogLevel = [
        Rgb(0x8C949F),   // info — quiet
        Rgb(0xF2CC4D),   // warning
        Rgb(0xF26152),   // error
    ];

    // Faint hairline for the in-panel surfaces: tree-connector guides, overlay borders. White at low
    // alpha so it reads on any surface in the ramp (a fixed grey would vanish on Bg0 and glare on Bg3).
    public static readonly SysVec4 Hairline = new(1f, 1f, 1f, 0.07f);   // overlay / panel border hairline
    public static readonly SysVec4 TreeGuide = new(1f, 1f, 1f, 0.16f);  // hierarchy tree-connector line

    // Modal-prompt surfaces (the asset-browser "New …" dialogs). Slightly raised popup over a recessed
    // input frame, pulled off the ramp so the prompts match the panels instead of re-typing hex.
    public static readonly SysVec4 PopupBg = Rgb(0x1F2127);   // modal/popup background (~Bg1, a touch lighter)
    public static readonly SysVec4 InputBg = Rgb(0x121419);   // recessed input frame inside a prompt

    // Convenience: PushFont(Header) for the duration of a using-less call site is awkward, so callers do
    // ImGui.PushFont(EditorTheme.Header); ...; ImGui.PopFont() directly. No wrapper needed.

    // Local hex helper so the palette can be authored as 0xRRGGBB without depending on ImGuiController.
    static SysVec4 Rgb(int hex) => new(
        ((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, 1f);
}
