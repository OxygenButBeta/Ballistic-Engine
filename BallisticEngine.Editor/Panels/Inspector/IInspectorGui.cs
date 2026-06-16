using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

// The immediate-mode GUI seam. The pipeline and every leaf drawer talk ONLY to this — never to ImGui
// directly. The real editor implements it twice (ImGuiComponentGui / ImGuiVolumeGui) which also own the
// host-specific chrome: the component grid row vs the volume override checkbox, per-widget undo, the
// mixed-value marker. Tests implement a recording fake. This abstraction is what makes the whole
// pipeline headlessly verifiable and lets one set of drawers serve both inspector paths.
public interface IInspectorGui {
    void PushId(string id);
    void PopId();

    void BeginDisabled();
    void EndDisabled();

    // Host-specific row scaffolding. The component adapter draws [label | value]; the volume adapter
    // draws [override-checkbox + label | value] and disables the value cell when not overridden.
    void BeginRow(IProperty property);
    void EndRow();

    // Chrome a decorator may emit above a row.
    void Header(string text);
    void Space(float height);
    void HelpBox(string text);

    // Value widgets — return true when edited this frame. Undo/dirty are the adapter's concern (the
    // ImGui adapters wrap these in InspectorUndo / mark the viewport dirty; the fake just records).
    bool Checkbox(ref bool v);
    bool SliderFloat(ref float v, float min, float max);
    bool DragFloat(ref float v, float speed);
    bool SliderInt(ref int v, int min, int max);
    bool DragInt(ref int v);
    bool InputText(ref string v, int maxLength);
    bool Combo(ref int index, string[] names);
    bool ColorEdit3(ref SysVec3 v, bool hdr);
    bool DragFloat2(ref System.Numerics.Vector2 v, float speed);
    bool DragFloat3(ref SysVec3 v, float speed);

    // No drawer matched the value type.
    void Unsupported(System.Type type);
}
