namespace BallisticEngine.Editor.Inspector;

internal static class InspectorLayout {
    static IEditorGui gui => EditorGui.Shared;

    public const float MinLabelWidth = 96f;

    public const float PreferredLabelWidth = 132f;

    public const float LabelValueGap = 10f;

    public const float DepthIndent = 12f;

    public static float ValueColumnLeft(float panelAvailWidth, float s) {
        float preferred = PreferredLabelWidth * s;
        float minLabel = MinLabelWidth * s;
        float maxLabel = System.Math.Max(minLabel, panelAvailWidth * 0.62f);
        return System.Math.Clamp(preferred, minLabel, maxLabel);
    }

    public static float LabelColumnWidth(int depth, float panelValueLeft, float s) {
        float labelW = panelValueLeft - DepthIndentTotal(depth, s);
        return System.Math.Max(MinLabelWidth * s * 0.5f, labelW);
    }

    public static float DepthIndentTotal(int depth, float s) => depth * DepthIndent * s;

    public static void DrawLabelCell(string label, int depth, float columnWidth, float s, string tooltip) {
        float indent = DepthIndentTotal(depth, s);
        if (indent > 0f)
            gui.Indent(indent);

        float avail = System.Math.Max(0f, columnWidth - indent - LabelValueGap * s);
        string shown = Ellipsize(label, avail);
        bool clipped = !ReferenceEquals(shown, label);

        gui.PushColor(EditorStyleColor.Text, EditorTheme.RowLabel);
        gui.TextUnformatted(shown);
        gui.PopColor();

        if (gui.IsItemHovered()) {
            if (tooltip is not null) gui.Tooltip(tooltip);
            else if (clipped) gui.Tooltip(label);
        }

        if (indent > 0f)
            gui.Unindent(indent);
    }

    public static string Ellipsize(string label, float maxWidth) {
        if (string.IsNullOrEmpty(label) || maxWidth <= 0f)
            return label;
        if (gui.CalcTextSize(label).X <= maxWidth)
            return label;

        const string ell = "…";
        float ellW = gui.CalcTextSize(ell).X;
        if (ellW >= maxWidth)
            return ell;

        int lo = 0, hi = label.Length;
        while (lo < hi) {
            int mid = (lo + hi + 1) >> 1;
            if (gui.CalcTextSize(label[..mid]).X + ellW <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return label[..lo] + ell;
    }

    public const int MemberSearchThreshold = 12;

    public const int ComponentSearchThreshold = 6;
}
