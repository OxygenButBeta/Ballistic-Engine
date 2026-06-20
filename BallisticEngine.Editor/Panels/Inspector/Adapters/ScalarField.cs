using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace BallisticEngine.Editor.Inspector;

// Double-click-to-type for numeric drag/slider widgets (Unity parity). ImGui's native escape from a
// DragFloat/SliderFloat into a text box is Ctrl+Click; users expect a double-click. This wraps both —
// when a row's widget is double-clicked we flip it into an InputFloat/InputInt for that ID until the
// edit commits (Enter / focus loss). Both inspector adapters (component + volume) route their scalar
// widgets through here, so the behaviour is identical everywhere with one implementation.
//
// State is keyed by the widget's ImGui ID (the "##v" pushed under each row's PushID), so it survives
// the per-frame immediate-mode redraw and is naturally unique per field. The value is a one-shot focus
// flag: true the first frame the box is shown (so we steal keyboard focus exactly once), false after.
internal static class ScalarField {
    static readonly Dictionary<uint, bool> editing = new();   // id -> needsFocus (true only on open frame)

    static void Open(uint id) => editing[id] = true;

    static bool IsEditing(uint id) => editing.ContainsKey(id);

    // Enter edit mode if the just-drawn drag/slider was double-clicked while hovered.
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

    // Draws the text box that replaces the drag/slider while typing. We focus it exactly once (the frame
    // it opens), then close it when it loses active focus (Enter or clicking away) — at which point the
    // row reverts to its drag/slider. NOTE: InputFloat/InputInt are InputScalar-based and ImGui ASSERTS
    // if you pass EnterReturnsTrue to them, so we DON'T — instead we report the edit via
    // IsItemDeactivatedAfterEdit (true once, on commit), which is the correct "value changed and the
    // user finished" signal for these widgets.
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
