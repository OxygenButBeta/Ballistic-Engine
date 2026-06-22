using System.Reflection;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public sealed class ImGuiComponentGui : IInspectorGui {
    static IEditorGui gui => EditorGui.Shared;

    readonly IComponentInspectorHost host;
    string label;

    public ImGuiComponentGui(IComponentInspectorHost host) => this.host = host;

    public void SetUndoLabel(string fullLabel) => label = fullLabel;

    public void PushId(string id) => gui.PushId(id);
    public void PopId() => gui.PopId();
    public void BeginDisabled() => gui.BeginDisabled();
    public void EndDisabled() => gui.EndDisabled();

    public void BeginRow(IProperty p) {
        host.RowWithTooltip(p.Label, p.Tooltip);
        if (p is MemberProperty mp) host.DrawMixedMarker(mp.Member, mp.Owner, mp.Get());
        gui.SetNextItemWidth(-1);
        label = $"Edit {p.Label}";
    }
    public void EndRow() { }

    public void Header(string t) => EditorDecoration.DrawSectionHeader(t);
    public void Space(float h) => gui.Dummy(new SysVec2(0, h));
    public void HelpBox(string t) => gui.TextWrapped(t);

    public bool Checkbox(ref bool v) {
        gui.PushFramePadding(new SysVec2(2, 2) * EditorTheme.UiScale);
        bool changed = host.TrackUndo(label, gui.Checkbox("##v", ref v));
        gui.PopStyleVar();
        return changed;
    }
    public bool SliderFloat(ref float v, float min, float max) {
        gui.PushColor(EditorStyleColor.SliderGrab, EditorTheme.SliderGrabRest);
        bool changed = host.TrackUndo(label, ScalarField.SliderFloat("##v", ref v, min, max, "%.3f"));
        gui.PopColor();
        return changed;
    }
    public bool DragFloat(ref float v, float speed) => host.TrackUndo(label, ScalarField.DragFloat("##v", ref v, speed, 0, 0, "%.3f"));
    public bool SliderInt(ref int v, int min, int max) {
        gui.PushColor(EditorStyleColor.SliderGrab, EditorTheme.SliderGrabRest);
        bool changed = host.TrackUndo(label, ScalarField.SliderInt("##v", ref v, min, max));
        gui.PopColor();
        return changed;
    }
    public bool DragInt(ref int v) => host.TrackUndo(label, ScalarField.DragInt("##v", ref v));
    public bool InputText(ref string v, int maxLength) => host.TrackUndo(label, gui.InputText("##v", ref v, maxLength));
    public bool Combo(ref int index, string[] names) => host.TrackUndo(label, gui.Combo("##v", ref index, names));
    public bool ColorEdit3(ref SysVec3 v, bool hdr) =>
        host.TrackUndo(label, hdr ? gui.ColorEdit3Hdr("##v", ref v) : gui.ColorEdit3("##v", ref v));
    public bool DragFloat2(ref SysVec2 v, float speed) => host.TrackUndo(label, gui.DragFloat2("##v", ref v, speed));
    public bool DragFloat3(ref SysVec3 v, float speed) => host.AxisVec3("v3", label, ref v, speed);
    public void Unsupported(Type t) => gui.TextDisabled($"({t.Name})");
}
