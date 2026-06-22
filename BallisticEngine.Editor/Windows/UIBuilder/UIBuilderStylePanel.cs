using BallisticEngine.UI;

namespace BallisticEngine.Editor;

internal sealed class UIBuilderStylePanel
{
    int _selectedRule = -1;
    string _newSelector = "";
    readonly UIBuilderInspector _ruleInspector = new();

    public void Draw(IEditorGui gui, UIBuilderDocument doc)
    {
        gui.PushFont(EditorFont.Bold); gui.Text("StyleSheet (USS)"); gui.PopFont();

        gui.SetNextItemWidth(gui.ContentRegionAvail.X * 0.55f);
        bool enter = gui.InputTextEnter("##newsel", ref _newSelector, 96);
        gui.SameLine();
        if ((gui.SmallButton("+ Rule") || enter) && !string.IsNullOrWhiteSpace(_newSelector))
        {
            string sel = _newSelector.Trim();
            if (!sel.StartsWith('.') && !sel.StartsWith('#') && !char.IsUpper(sel[0])) sel = "." + sel;
            doc.AddRule(sel);
            _selectedRule = doc.Rules.Count - 1;
            _newSelector = "";
        }

        gui.BeginChild("##rulelist", new Vector2(0, 110 * gui.Scale), border: true);
        for (int i = 0; i < doc.Rules.Count; i++)
        {
            gui.PushId(i);
            var rule = doc.Rules[i];
            bool sel = i == _selectedRule;
            if (gui.SmallButton(EditorIcons.Cancel)) { doc.RemoveRule(i); gui.PopId(); _selectedRule = -1; break; }
            gui.SameLine();
            if (gui.Selectable(rule.Selector, sel)) _selectedRule = i;
            gui.PopId();
        }
        gui.EndChild();

        if (_selectedRule >= 0 && _selectedRule < doc.Rules.Count)
        {
            var rule = doc.Rules[_selectedRule];
            gui.Separator();
            string selName = rule.Selector;
            gui.SetNextItemWidth(-1);
            if (gui.InputTextEnter("Selector", ref selName, 96) && !string.IsNullOrWhiteSpace(selName))
                doc.RenameRule(_selectedRule, selName.Trim());

            gui.TextDisabled("Rule body");
            DrawRuleBody(gui, doc, _selectedRule, rule.Carrier);

            if (gui.Button("Apply to selection", new Vector2(-1, 0)) && doc.Selection != null)
            {
                string cls = rule.Selector.TrimStart('.');
                if (rule.Selector.StartsWith('.')) doc.AddClass(doc.Selection, cls.Split(':')[0]);
            }
        }
        else
        {
            gui.TextDisabled("Select a rule to edit, or add one. Tip: `.card`, `.btn:hover`, `Button`.");
        }
    }

    void DrawRuleBody(IEditorGui gui, UIBuilderDocument doc, int ruleIndex, VisualElement carrier)
    {
        var s = carrier.Style;
        bool changed = false;

        changed |= ColorRow(gui, "Background", s.BackgroundColor, c => s.BackgroundColor = c);
        changed |= ColorRow(gui, "Text Color", s.TextColor, c => s.TextColor = c);
        changed |= ColorRow(gui, "Border Color", s.BorderColor, c => s.BorderColor = c);

        float fs = s.FontSize; if (Row(gui, () => gui.DragFloat("Font Size", ref fs, 0.5f, 1, 256))) { s.FontSize = fs; changed = true; }
        float rad = s.BorderRadiusTopLeft; if (Row(gui, () => gui.DragFloat("Corner Radius", ref rad, 0.5f, 0, 999))) { s.BorderRadius = rad; changed = true; }
        float bw = s.BorderWidthVisual; if (Row(gui, () => gui.DragFloat("Border Width", ref bw, 0.2f, 0, 64))) { s.SetBorderWidth(Edge.All, bw); changed = true; }
        float op = s.Opacity; if (Row(gui, () => gui.SliderFloat("Opacity", ref op, 0, 1, "%.2f"))) { s.Opacity = op; changed = true; }
        float pad = NzPad(s); if (Row(gui, () => gui.DragFloat("Padding", ref pad, 0.5f, 0, 999))) { s.Padding = pad; changed = true; }

        if (gui.IsAnyItemActive() && !_ruleGesture) { doc.PushUndo(); _ruleGesture = true; }
        if (!gui.IsAnyItemActive()) _ruleGesture = false;
        if (changed) doc.RuleEdited(ruleIndex);
    }
    bool _ruleGesture;

    static float NzPad(Style s) { float v = s.GetPadding(Edge.Left); return float.IsNaN(v) ? 0 : v; }

    static bool Row(IEditorGui gui, Func<bool> w) => w();

    static bool ColorRow(IEditorGui gui, string label, Color cur, Action<Color> apply)
    {
        Vector4 v = new(cur.R, cur.G, cur.B, cur.A);
        if (gui.ColorEdit4(label, ref v)) { apply(new Color(v.X, v.Y, v.Z, v.W)); return true; }
        return false;
    }
}
