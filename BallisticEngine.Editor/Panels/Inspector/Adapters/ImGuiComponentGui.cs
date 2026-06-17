using System;
using System.Reflection;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

// INTEGRATION-TIME (compiled in the real editor against Hexa.NET.ImGui; NOT in the headless test).
// Bridges InspectorPanel's existing helpers to the shared IInspectorGui so the component path drops its
// hardcoded switch. Value widgets wrap InspectorUndo.Track exactly like the old DrawMember did -> undo +
// dirty stay byte-identical. The host (InspectorPanel) implements IComponentInspectorHost by forwarding
// to its own helpers (BeginGrid/RowWithTooltip/AxisVec3 are static; ApplyMember/DrawMixedMarker/
// DrawAssetSlot/MarkViewportDirty are instance; TrackUndo => InspectorUndo.Track).
public interface IComponentInspectorHost {
    void RowWithTooltip(string label, string tooltip);
    void DrawMixedMarker(MemberInfo member, object target, object value);
    bool AxisVec3(string id, string label, ref SysVec3 v, float speed);
    bool TrackUndo(string label, bool changed);
    void MarkViewportDirty();

    // editor-rework B4: the BObject asset-slot terminal drawer (AssetSlotDrawer) routes its IProperty here so
    // the slot's existing drag-drop + picker rendering (InspectorPanel.DrawAssetSlot) is reused unchanged --
    // the host unwraps the property's member/owner/type and forwards to its private DrawAssetSlot.
    void DrawAssetSlot(IProperty property);

    // editor-rework G1-editor (Rule 1, the visible half of the EntityRef/ComponentRef work; engine half done
    // in ch17): the scene-object-ref terminal drawer (SceneObjectRefDrawer) routes an EntityRef / ComponentRef
    // member here. The host renders an interactive scene-object SLOT (current target name + drag-onto-slot from
    // a Hierarchy row + a searchable picker of live scene entities / behaviours) in place of the dead
    // `(EntityRef)` / `(ComponentRef)` disabled label these members fell to via gui.Unsupported. Mirrors the
    // DrawAssetSlot host-method shape exactly; the host unwraps the IProperty and renders + sets the ref.
    void DrawSceneObjectSlot(IProperty property);

    // editor-rework G2-editor (Rule 2, the visible half of the List<T>/T[] round-trip; engine half done in
    // ch19): the collection terminal drawer (CollectionDrawer) routes a List<T> / T[] member here. The host
    // renders an interactive collection editor (count + Add, per-element row with a Remove button, each
    // element drawn RECURSIVELY by its own terminal drawer) in place of the dead `(List`1)` / `(...)`
    // disabled label these members fell to via gui.Unsupported. Mirrors the DrawAssetSlot / DrawSceneObjectSlot
    // host-method shape; the host unwraps the IProperty, mutates the backing collection, writes it back through
    // the property (-> ApplyMember multi-select broadcast + dirty), and pushes one undo per add / remove / edit.
    void DrawCollectionSlot(IProperty property);

    // editor-rework G2-editor (ch21, the visible half of the Dictionary<K,V> round-trip; engine half done in
    // ch19): the dictionary terminal drawer (DictionaryDrawer) routes a Dictionary<K,V> member here. The host
    // renders an interactive dictionary editor (count + Add, per-entry row with a READ-ONLY key + a value drawn
    // RECURSIVELY by its own terminal drawer + a Remove button) in place of the dead `(Dictionary`2)` disabled
    // label these members fell to via gui.Unsupported. Mirrors the DrawCollectionSlot host-method shape; the
    // host unwraps the IProperty, mutates the backing dictionary, writes it back through the property (->
    // ApplyMember multi-select broadcast + dirty), and pushes one undo per add / remove / value edit.
    void DrawDictionarySlot(IProperty property);
}

public sealed class ImGuiComponentGui : IInspectorGui {
    readonly IComponentInspectorHost host;
    string label;

    public ImGuiComponentGui(IComponentInspectorHost host) => this.host = host;

    // Since B0 the component value rows route through the shared DrawerStack, so BeginRow (below) sets the
    // undo label ("Edit {label}") just like the volume path — DrawMember no longer calls this. Kept for any
    // host that wants to override the label before a manual drawer call (none today; harmless to retain).
    public void SetUndoLabel(string fullLabel) => label = fullLabel;

    public void PushId(string id) => ImGui.PushID(id);
    public void PopId() => ImGui.PopID();
    public void BeginDisabled() => ImGui.BeginDisabled();
    public void EndDisabled() => ImGui.EndDisabled();

    public void BeginRow(IProperty p) {
        host.RowWithTooltip(p.Label, p.Tooltip);
        if (p is MemberProperty mp) host.DrawMixedMarker(mp.Member, mp.Owner, mp.Get());
        ImGui.SetNextItemWidth(-1);
        label = $"Edit {p.Label}";
    }
    public void EndRow() { }

    public void Header(string t) => ImGui.SeparatorText(t);
    public void Space(float h) => ImGui.Dummy(new SysVec2(0, h));
    public void HelpBox(string t) => ImGui.TextWrapped(t);

    public bool Checkbox(ref bool v) => host.TrackUndo(label, ImGui.Checkbox("##v", ref v));
    public bool SliderFloat(ref float v, float min, float max) => host.TrackUndo(label, ImGui.SliderFloat("##v", ref v, min, max));
    public bool DragFloat(ref float v, float speed) => host.TrackUndo(label, ImGui.DragFloat("##v", ref v, speed));
    public bool SliderInt(ref int v, int min, int max) => host.TrackUndo(label, ImGui.SliderInt("##v", ref v, min, max));
    public bool DragInt(ref int v) => host.TrackUndo(label, ImGui.DragInt("##v", ref v));
    public bool InputText(ref string v, int maxLength) => host.TrackUndo(label, ImGui.InputText("##v", ref v, (uint)maxLength));
    public bool Combo(ref int index, string[] names) => host.TrackUndo(label, ImGui.Combo("##v", ref index, names, names.Length));
    public bool ColorEdit3(ref SysVec3 v, bool hdr) =>
        host.TrackUndo(label, ImGui.ColorEdit3("##v", ref v, hdr ? ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float : ImGuiColorEditFlags.None));
    public bool DragFloat2(ref SysVec2 v, float speed) => host.TrackUndo(label, ImGui.DragFloat2("##v", ref v, speed));
    public bool DragFloat3(ref SysVec3 v, float speed) => host.AxisVec3("v3", label, ref v, speed); // AxisVec3 owns its undo
    public void Unsupported(Type t) => ImGui.TextDisabled($"({t.Name})");
}
