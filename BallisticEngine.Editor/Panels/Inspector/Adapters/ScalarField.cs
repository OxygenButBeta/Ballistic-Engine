using Hexa.NET.ImGui;

namespace BallisticEngine.Editor.Inspector;

internal static class ScalarField {
    static readonly Dictionary<uint, bool> editing = new();

    static void Open(uint id) => editing[id] = true;

    static bool IsEditing(uint id) => editing.ContainsKey(id);

    static void MaybeOpen(uint id) {
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Open(id);
    }

    public static bool DragFloat(string label, ref float v, float speed, float min, float max, string format) {
        uint id = ImGui.GetID(label);
        if (IsEditing(id)) return TypeFloat(label, id, ref v, format);
        bool changed = ImGui.DragFloat(label, ref v, speed, min, max, format);
        MaybeOpen(id);
        return changed;
    }

    public static bool SliderFloat(string label, ref float v, float min, float max, string format) {
        uint id = ImGui.GetID(label);
        if (IsEditing(id)) return TypeFloat(label, id, ref v, format);
        bool changed = ImGui.SliderFloat(label, ref v, min, max, format);
        MaybeOpen(id);
        return changed;
    }

    public static bool DragInt(string label, ref int v) {
        uint id = ImGui.GetID(label);
        if (IsEditing(id)) return TypeInt(label, id, ref v);
        bool changed = ImGui.DragInt(label, ref v);
        MaybeOpen(id);
        return changed;
    }

    public static bool SliderInt(string label, ref int v, int min, int max) {
        uint id = ImGui.GetID(label);
        if (IsEditing(id)) return TypeInt(label, id, ref v);
        bool changed = ImGui.SliderInt(label, ref v, min, max);
        MaybeOpen(id);
        return changed;
    }

    static bool TypeFloat(string label, uint id, ref float v, string format) {
        bool needsFocus = editing[id];
        if (needsFocus) { ImGui.SetKeyboardFocusHere(); editing[id] = false; }
        ImGui.InputFloat(label, ref v, 0, 0, format);
        bool committed = ImGui.IsItemDeactivatedAfterEdit();
        if (!needsFocus && !ImGui.IsItemActive())
            editing.Remove(id);
        return committed;
    }

    static bool TypeInt(string label, uint id, ref int v) {
        bool needsFocus = editing[id];
        if (needsFocus) { ImGui.SetKeyboardFocusHere(); editing[id] = false; }
        ImGui.InputInt(label, ref v, 0, 0);
        bool committed = ImGui.IsItemDeactivatedAfterEdit();
        if (!needsFocus && !ImGui.IsItemActive())
            editing.Remove(id);
        return committed;
    }
}
