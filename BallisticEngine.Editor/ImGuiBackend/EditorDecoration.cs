using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Phase E (RW4) — a tiny, shared DrawList primitive library: the "crafted surface" affordances the user
// wanted ("biraz dekorasyon ekle, ama killememeli"). Before this, every panel that wanted a card / divider /
// badge hand-rolled its own AddRectFilled + AddRect with hex literals typed inline (the entity header card,
// the component-header stripe, etc.), so the look couldn't be tuned in one place and drifted between panels.
//
// EditorDecoration is PURELY geometry: it draws on the current window's draw list and pulls every COLOR from
// EditorTheme's surface palette (Bg0..BorderLight + the row accent helpers) — it does NOT open a new parallel
// color source (plan §9 GOTCHA for RW4). Callers keep owning layout (rects, cursor, IDs); this only paints.
//
// PERFORMANCE (plan §4, first-class constraint): each primitive is a couple of AddRectFilled / AddLine /
// AddText calls — NO per-row gradient or drop-shadow, NO allocation, NO reflection. They are meant to be
// called a handful of times per panel (card backgrounds, section dividers, the odd badge), never once per
// member row at scale (the per-row hover affordance already lives in InspectorPanel.RowChrome and stays
// hover-gated). Decoration is budgeted: prefer style-driven where equivalent, reach for DrawList only for the
// surfaces ImGui's stock widgets can't express (a card behind a composite header, a colored pill).
internal static class EditorDecoration {
    // --- Cards ----------------------------------------------------------------------------------------
    // A raised "surface" panel: a filled rounded rect with a subtle inner border. The fill is a faint white
    // wash over whatever sits behind (so it reads as elevation on any panel bg without re-deriving the exact
    // graphite hue); the border is the palette's hairline. One AddRectFilled + one AddRect.
    public static void DrawCard(SysVec2 min, SysVec2 max, float rounding = 6f) {
        var draw = ImGui.GetWindowDrawList();
        // Faint white wash = "lift" over the panel background. Tuned to match the historical entity-header
        // card (white @ ~0.035) so the change is centralisation, not a restyle.
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.035f)), rounding);
        draw.AddRect(min, max, ImGui.GetColorU32(BorderColor(0.55f)), rounding);
    }

    // A card with an accent-tinted left stripe (the same affordance the component header uses): a card that
    // also signals a category/color. `accent` is the stripe color (e.g. a component category tint).
    public static void DrawCardWithStripe(SysVec2 min, SysVec2 max, SysVec4 accent, float rounding = 6f) {
        DrawCard(min, max, rounding);
        DrawAccentStripe(min, max.Y - min.Y, accent);
    }

    // --- Accent stripe --------------------------------------------------------------------------------
    // A thin vertical color bar at the LEFT edge of a header/row (the component-header category stripe,
    // generalised). `topLeft` is the bar's top-left; `height` its height. Width is fixed + small.
    public const float StripeWidth = 3f;
    public static void DrawAccentStripe(SysVec2 topLeft, float height, SysVec4 color) {
        ImGui.GetWindowDrawList().AddRectFilled(
            topLeft, new SysVec2(topLeft.X + StripeWidth, topLeft.Y + height), ImGui.GetColorU32(color));
    }

    // --- Section header -------------------------------------------------------------------------------
    // A lightweight section title with a divider rule trailing the label (depth via the palette's hairline,
    // not a heavy SeparatorText box). Draws the label in the Caption color + advances the cursor. Use for
    // grouping sub-sections inside a panel where a full framed CollapsingHeader would be too loud.
    public static void DrawSectionHeader(string label) {
        // Right edge of the content region BEFORE the label is drawn (cursor X + remaining avail width) —
        // GetContentRegionAvail is the binding's portable way to find it (GetWindowContentRegionMax isn't
        // exposed in this Hexa.NET.ImGui build).
        float contentRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.RowCaption);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        // Trailing rule from just past the label to the content edge.
        SysVec2 rectMin = ImGui.GetItemRectMin();
        SysVec2 rectMax = ImGui.GetItemRectMax();
        float midY = (rectMin.Y + rectMax.Y) * 0.5f;
        float x0 = rectMax.X + 8f;
        if (contentRight > x0 + 4f)
            ImGui.GetWindowDrawList().AddLine(
                new SysVec2(x0, midY), new SysVec2(contentRight, midY), ImGui.GetColorU32(BorderColor(1f)));
    }

    // --- Divider --------------------------------------------------------------------------------------
    // A subtle full-width horizontal rule in the palette's BorderLight hairline (replaces a stock Separator
    // when a quieter, palette-consistent line is wanted). Advances the cursor by `padY` on each side.
    public static void DrawDivider(float padY = 3f) {
        ImGui.Dummy(new SysVec2(0, padY));
        SysVec2 p = ImGui.GetCursorScreenPos();
        float x0 = p.X;                                 // current cursor = content left edge
        float x1 = p.X + ImGui.GetContentRegionAvail().X;  // + remaining width = content right edge
        ImGui.GetWindowDrawList().AddLine(
            new SysVec2(x0, p.Y), new SysVec2(x1, p.Y), ImGui.GetColorU32(BorderColor(1f)));
        ImGui.Dummy(new SysVec2(0, padY));
    }

    // --- Badge / chip ---------------------------------------------------------------------------------
    // A small rounded pill with centered text (a count chip, a status tag). Draws at the current cursor and
    // reserves its footprint in the layout via a Dummy, so callers can SameLine before/after it. The pill
    // fill is `color` (typically a low-alpha accent); text is the palette's bright Text. One AddRectFilled
    // (+ rounded) + one AddText. Returns the advanced width.
    public static float DrawBadge(string text, SysVec4 color) {
        var draw = ImGui.GetWindowDrawList();
        SysVec2 ts = ImGui.CalcTextSize(text);
        float padX = 6f, padY = 2f;
        SysVec2 min = ImGui.GetCursorScreenPos();
        SysVec2 size = new(ts.X + padX * 2, ts.Y + padY * 2);
        SysVec2 max = min + size;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), size.Y * 0.5f);
        draw.AddText(new SysVec2(min.X + padX, min.Y + padY), ImGui.GetColorU32(EditorTheme.Text), text);
        ImGui.Dummy(size);
        return size.X;
    }

    // --- Empty-state card -----------------------------------------------------------------------------
    // A faint dashed-feel placeholder card (no real dashing — a low-alpha rounded outline) for "nothing here
    // yet" regions. Just the outline; the caller positions its centered icon/text inside.
    public static void DrawEmptyCard(SysVec2 min, SysVec2 max, float rounding = 8f) {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.012f)), rounding);
        draw.AddRect(min, max, ImGui.GetColorU32(BorderColor(0.7f)), rounding);
    }

    // The palette's hairline border at a chosen alpha (so a card outline can be a touch softer/harder than a
    // divider while staying on the SAME source color — no second hex literal).
    static SysVec4 BorderColor(float alpha) => new(
        EditorTheme.BorderLight.X, EditorTheme.BorderLight.Y, EditorTheme.BorderLight.Z, alpha);
}
