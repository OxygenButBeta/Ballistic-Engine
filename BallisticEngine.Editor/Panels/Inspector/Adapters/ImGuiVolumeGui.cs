using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public sealed class ImGuiVolumeGui : IInspectorGui {
    static IEditorGui gui => EditorGui.Shared;

    bool gatedByOverride;
    bool overrideChanged;

    public bool TakeOverrideChanged() { bool c = overrideChanged; overrideChanged = false; return c; }

    public void PushId(string id) => gui.PushId(id);
    public void PopId() => gui.PopId();
    public void BeginDisabled() => gui.BeginDisabled();
    public void EndDisabled() => gui.EndDisabled();

    public void BeginRow(IProperty p) {
        gui.TableNextRow();
        gui.TableSetColumnIndex(0);

        bool overridden = p.Overridden;
        gui.PushFramePadding(new SysVec2(2, 2) * EditorTheme.UiScale);
        if (gui.Checkbox("##override", ref overridden)) { p.Overridden = overridden; overrideChanged = true; }
        gui.PopStyleVar();
        if (gui.IsItemHovered())
            gui.Tooltip(overridden ? "Overriding. Click to use the default." : "Click to override this parameter.");

        gui.SameLine();
        gui.AlignTextToFramePadding();
        gui.TextDisabled(p.Label);
        if (p.Tooltip is not null && gui.IsItemHovered())
            gui.Tooltip(p.Tooltip);

        gui.TableSetColumnIndex(1);
        gui.SetNextItemWidth(-1);

        gatedByOverride = !p.Overridden;
        if (gatedByOverride) gui.BeginDisabled();
    }

    public void EndRow() { if (gatedByOverride) gui.EndDisabled(); }

    public void Header(string t) { }
    public void Space(float h) { }
    public void HelpBox(string t) { gui.TextDisabled(t); }

    public bool Checkbox(ref bool v) {
        gui.PushFramePadding(new SysVec2(2, 2) * EditorTheme.UiScale);
        bool changed = gui.Checkbox("##v", ref v);
        gui.PopStyleVar();
        return changed;
    }
    public bool SliderFloat(ref float v, float min, float max) {
        gui.PushColor(EditorStyleColor.SliderGrab, EditorTheme.SliderGrabRest);
        bool changed = ScalarField.SliderFloat("##v", ref v, min, max, "%.3f");
        gui.PopColor();
        return changed;
    }
    public bool DragFloat(ref float v, float speed) => ScalarField.DragFloat("##v", ref v, speed, 0, 0, "%.3f");
    public bool SliderInt(ref int v, int min, int max) {
        gui.PushColor(EditorStyleColor.SliderGrab, EditorTheme.SliderGrabRest);
        bool changed = ScalarField.SliderInt("##v", ref v, min, max);
        gui.PopColor();
        return changed;
    }
    public bool DragInt(ref int v) => ScalarField.DragInt("##v", ref v);
    public bool InputText(ref string v, int maxLength) => gui.InputText("##v", ref v, maxLength);
    public bool Combo(ref int index, string[] names) => gui.Combo("##v", ref index, names);
    public bool ColorEdit3(ref SysVec3 v, bool hdr) =>
        hdr ? gui.ColorEdit3Hdr("##v", ref v) : gui.ColorEdit3("##v", ref v);
    public bool DragFloat2(ref System.Numerics.Vector2 v, float speed) => gui.DragFloat2("##v", ref v, speed);
    public bool DragFloat3(ref SysVec3 v, float speed) => gui.DragFloat3("##v", ref v, speed);
    public void Unsupported(Type t) => gui.TextDisabled($"({t.Name})");
}
