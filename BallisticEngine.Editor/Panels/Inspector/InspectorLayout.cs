using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor.Inspector;

// EF-LAYOUT — the ONE inspector layout model the inspector member grid is built on. This is the shared
// helper the review (plan §EF-LAYOUT) asked for so EF16 (nesting indent), EF11 (adaptive label column),
// and EF10 (per-component member search) implement against the SAME column rules instead of contradicting
// each other (EF16 wants the value box at a fixed x independent of depth; EF11 wants an adaptive label
// column; today's BeginGrid uses a proportional split that does neither and clips long labels / shrinks
// the value column at every nesting level).
//
// ─── THE COLUMN MODEL (the contract EF16/EF11/EF10 must honour) ───────────────────────────────────────
//  • Two columns: a LABEL/foldout column on the left, a VALUE-field column on the right.
//  • The value column's LEFT EDGE is anchored at a FIXED x within the panel — `ValueColumnLeft(...)` — and
//    does NOT move with nesting depth. A `list → element → struct → field` chain keeps full-width value
//    boxes at every depth (EF16). This is the opposite of the old behaviour, where a nested grid lived
//    inside a TreeNode's full IndentSpacing and marched the WHOLE table (both columns) right per level.
//  • DEPTH indents the LABEL/foldout only, by a SMALL fixed step (`DepthIndent`), never the value column.
//    A nested grid therefore narrows its own LABEL column by the indent so the value edge still lands at
//    the panel-level value-x (`LabelColumnWidth` does this arithmetic).
//  • The label column is ADAPTIVE within `[MinLabelWidth*S, valueLeft − Gap*S]`: it gets the natural label
//    width when that fits, clamped to the band otherwise, with ELLIPSIS + a full-text hover tooltip on
//    overflow (EF11) so a long label like "High Speed Steer Scale" is never silently truncated.
//  • A per-component member SEARCH BAR (EF10a) sits ABOVE this grid and only filters which rows draw — it
//    is not part of the column model; it just decides row visibility before the grid runs.
//
// ─── SCOPE OF THIS CHUNK (EF-LAYOUT) ──────────────────────────────────────────────────────────────────
// This file is the design note + the shared metrics/primitives ONLY. It deliberately does NOT rewire
// InspectorPanel.BeginGrid / DrawNestedSlot / DrawPolymorphicSlot yet — that is each implementer chunk's
// slice (EF16 first, then EF11, then EF10), per the plan's sequence. Until a call site opts in by calling
// these helpers, the inspector draws EXACTLY as before (byte-identical for the existing short-label /
// shallow components — the plan's hard constraint). The constants below are the single place those three
// chunks read their metrics from, so they cannot drift apart.
//
// PERFORMANCE (plan §4): pure arithmetic + the existing ImGui calls. No per-frame reflection / allocation;
// the ellipsis path only runs when a label actually overflows its column.
internal static class InspectorLayout {
    // --- Metrics (pre-DPI-scale; multiply by the caller's UI scale `S`) -------------------------------
    // The smallest the adaptive label column may shrink to before it starts ellipsing. Roomy enough for a
    // short word ("Size", "Mass") at the body font; long labels ellipse rather than push past it.
    public const float MinLabelWidth = 96f;

    // The label column's PREFERRED width when the panel is wide enough — the value box starts here. Chosen
    // to match the visual weight of the old 0.38 proportional split at a typical inspector width (~340px
    // panel → ~130px label), but as a FIXED anchor so the value edge is stable across depth and panel size.
    public const float PreferredLabelWidth = 132f;

    // Gap between the label column's right edge and the value field's left edge (breathing room so an
    // ellipsed label doesn't touch the widget).
    public const float LabelValueGap = 10f;

    // Per-depth indent applied to the LABEL/foldout ONLY (never the value column). Small on purpose — just
    // enough to read the nesting, NOT ImGui's full IndentSpacing (~21px) which is what marched the value
    // box off-screen. One step per nesting level (struct-in-list-in-struct …).
    public const float DepthIndent = 12f;

    // --- Column arithmetic ----------------------------------------------------------------------------
    // ANCHOR: the value field's left edge, as a width measured from the TOP-LEVEL grid's content-left edge
    // (i.e. the depth-0 left). `panelAvailWidth` is the content width available to the top-level grid
    // (ImGui.GetContentRegionAvail().X at the component's grid). This is the ONE value-x the whole component
    // — every depth — aligns its value boxes to. Clamped so it never eats more than ~62% of a narrow panel
    // (a thin inspector still shows a usable value box) nor leaves a wastefully wide label column when wide.
    public static float ValueColumnLeft(float panelAvailWidth, float s) {
        float preferred = PreferredLabelWidth * s;
        float minLabel = MinLabelWidth * s;
        float maxLabel = System.Math.Max(minLabel, panelAvailWidth * 0.62f);
        return System.Math.Clamp(preferred, minLabel, maxLabel);
    }

    // The width to give the LABEL column at `depth` so the value field's left edge lands at the SAME
    // panel-level value-x (`ValueColumnLeft`) regardless of depth. A nested grid's content-left is already
    // shifted right by `depth` indent steps, so the nested label column must be the value-x MINUS that
    // shift. `panelValueLeft` is the top-level ValueColumnLeft (the caller computes it once per component
    // and threads it down). Clamped to a sane minimum so a very deep nesting still leaves a usable column.
    public static float LabelColumnWidth(int depth, float panelValueLeft, float s) {
        float labelW = panelValueLeft - DepthIndentTotal(depth, s);
        return System.Math.Max(MinLabelWidth * s * 0.5f, labelW);
    }

    // Total label indent accumulated by `depth` nesting levels (depth 0 == top-level member, no indent).
    public static float DepthIndentTotal(int depth, float s) => depth * DepthIndent * s;

    // --- Label cell -----------------------------------------------------------------------------------
    // Draw a member label into the current label cell at `depth`, honouring the column model: a small fixed
    // per-depth indent on the label only, ellipsis when the label is wider than `columnWidth`, and a
    // full-text hover tooltip whenever the label was clipped OR a real [Tooltip] string is supplied. This
    // is the single primitive EF11 (adaptive column / legible labels) and EF16 (depth indent) route every
    // member label through, so the two rules live in one place and can't disagree.
    //
    // `columnWidth` is the label column's pixel width at this depth (from LabelColumnWidth, already
    // depth-shifted); `tooltip` is the member's [Tooltip] text or null. Caller has already positioned the
    // cursor at the cell's left edge and called AlignTextToFramePadding(). Pushes/pops its own text color
    // (RowLabel). The text gets `columnWidth − gap` to draw in; the per-depth indent is applied here on the
    // label only (the value column never sees it).
    public static void DrawLabelCell(string label, int depth, float columnWidth, float s, string tooltip) {
        float indent = DepthIndentTotal(depth, s);
        if (indent > 0f)
            ImGui.Indent(indent);

        // The text occupies [indent .. columnWidth − gap]; subtract BOTH so a deeply-indented label still
        // ellipsizes before it touches the value field (EF11 — `columnWidth` is the cell's full width measured
        // before the indent, so the budget must drop the indent the cursor just consumed).
        float avail = System.Math.Max(0f, columnWidth - indent - LabelValueGap * s);
        string shown = Ellipsize(label, avail);
        bool clipped = !ReferenceEquals(shown, label);

        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.RowLabel);
        ImGui.TextUnformatted(shown);
        ImGui.PopStyleColor();

        // EF11: a clipped label is never silently lost — hovering it shows the full text. A real [Tooltip]
        // wins (shows the explanation); a clip with no tooltip shows the full label.
        if (ImGui.IsItemHovered()) {
            if (tooltip is not null) ImGui.SetTooltip(tooltip);
            else if (clipped) ImGui.SetTooltip(label);
        }

        if (indent > 0f)
            ImGui.Unindent(indent);
    }

    // Returns `label` unchanged when it fits in `maxWidth`, otherwise the longest prefix that fits with a
    // trailing "…" appended. Reference-equality with the input signals "not clipped" to the caller (so we
    // avoid a second CalcTextSize). A binary search keeps it O(log n) per overflowing label.
    public static string Ellipsize(string label, float maxWidth) {
        if (string.IsNullOrEmpty(label) || maxWidth <= 0f)
            return label;
        if (ImGui.CalcTextSize(label).X <= maxWidth)
            return label;                                   // fits — caller sees ReferenceEquals == true

        const string ell = "…";
        float ellW = ImGui.CalcTextSize(ell).X;
        if (ellW >= maxWidth)
            return ell;                                     // column too thin even for the ellipsis glyph

        int lo = 0, hi = label.Length;
        while (lo < hi) {
            int mid = (lo + hi + 1) >> 1;
            if (ImGui.CalcTextSize(label[..mid]).X + ellW <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return label[..lo] + ell;
    }

    // --- Search-bar gate (EF10a metric) ---------------------------------------------------------------
    // The per-component member search bar (EF10a) is CONDITIONAL — it only shows on components with enough
    // members to be worth filtering (don't clutter a 3-field component). EF10a reads this threshold so the
    // "show the search box?" rule lives with the rest of the layout model. Tunable in EF10a.
    public const int MemberSearchThreshold = 12;

    // --- Component-list search-bar gate (EF10b metric) ------------------------------------------------
    // The top-of-inspector component-LIST search (EF10b) is also CONDITIONAL — it only shows once an entity
    // carries enough components to be worth filtering (a 2-3 component entity doesn't need a box above the
    // first header). Coarser than the per-component member threshold because the unit (a whole component)
    // is bigger. Tunable in EF10b.
    public const int ComponentSearchThreshold = 6;
}
