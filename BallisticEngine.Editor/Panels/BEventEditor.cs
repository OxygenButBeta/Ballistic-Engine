using System.Reflection;
using BallisticEngine.AssetPipeline;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Inspector editor for a BEvent member — the Ballistic equivalent of Unity's UnityEvent drawer.
// Renders the list of persistent listeners (target picker, method dropdown, call mode, and a static
// argument widget when the chosen method takes one) plus +/- buttons. Each structural change pushes
// one whole-scene undo snapshot (BEvents now serialize, so undo round-trips them).
//
// Target = an Entity in the current scene, or a Behaviour on it; the method dropdown lists that
// target's invokable methods (BEventReflection) split by Void / 1-arg, with a "(Dynamic <T>)" group
// when the event carries a runtime value (BEvent<T>). Pure ImGui; no engine mutation outside the
// listener list it's handed.
internal static class BEventEditor {

    // Draws the whole BEvent block (header already drawn by the caller's Row). Returns true if any
    // listener was changed (the caller doesn't need it today, but it mirrors the other drawers).
    public static bool Draw(string id, BEvent evt) {
        if (evt is null) {
            ImGui.TextDisabled("(null event)");
            return false;
        }

        ImGui.PushID(id);
        var changed = false;

        Type dynamicType = evt.DynamicArgType;
        string subtitle = dynamicType is null ? "Event" : $"Event<{Pretty(dynamicType)}>";
        ImGui.TextDisabled($"{subtitle} — {evt.PersistentListeners.Count} listener(s)");

        for (var i = 0; i < evt.PersistentListeners.Count; i++) {
            ImGui.PushID(i);
            PersistentListener listener = evt.PersistentListeners[i];

            if (DrawListenerCard(listener, dynamicType, out bool removeRequested))
                changed = true;

            if (removeRequested) {
                EditorUndo.Push("Remove Event Listener");
                evt.PersistentListeners.RemoveAt(i);
                i--;
                changed = true;
                ImGui.PopID();
                continue;
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (ImGui.Button($"{EditorIcons.Add}  Add Listener", new SysVec2(-1, 0))) {
            EditorUndo.Push("Add Event Listener");
            evt.PersistentListeners.Add(new PersistentListener());
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    // One listener row, drawn as a bordered card: [target] [method] on the first line, call-mode +
    // static-arg on the second, a remove button on the right. Returns whether anything changed.
    static bool DrawListenerCard(PersistentListener listener, Type dynamicType, out bool removeRequested) {
        removeRequested = false;
        var changed = false;

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(6, 3));
        // AutoResizeY sizes the card to its content (two lines + the optional arg widget); the Y in
        // the size vector is ignored when AutoResizeY is set.
        ImGui.BeginChild("card", new SysVec2(0, 0),
            ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);

        // ---- Line 1: target + remove ----------------------------------------
        float removeW = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - removeW - 6);
        if (DrawTargetPicker(listener))
            changed = true;
        ImGui.SameLine();
        if (EditorIcons.GhostButton("rm", EditorIcons.Delete, "Remove listener", removeW))
            removeRequested = true;

        // ---- Line 2: method + mode ------------------------------------------
        BObject target = listener.ResolveTarget();
        if (target is null) {
            ImGui.TextDisabled(listener.TargetId == Guid.Empty
                ? "Pick a target to choose a method."
                : "Target not found in this scene.");
        }
        else {
            if (DrawMethodPicker(listener, target, dynamicType))
                changed = true;

            // Static argument widget, only when the bound method takes one in Static mode.
            if (listener.Mode == PersistentListener.CallMode.Static && listener.StaticArgumentType is not null) {
                ImGui.SetNextItemWidth(-1);
                if (DrawStaticArg(listener))
                    changed = true;
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        return changed;
    }

    // Target picker: a button showing the current target (entity name, or "Entity / Component"),
    // opening a popup that lists every entity, each expandable to its components.
    static bool DrawTargetPicker(PersistentListener listener) {
        BObject current = listener.ResolveTarget();
        string label = current switch {
            null when listener.TargetId == Guid.Empty => $"None  {EditorIcons.ChevronDown}",
            null => $"(missing)  {EditorIcons.ChevronDown}",
            Entity e => $"{EditorIcons.Package}  {e.Name}  {EditorIcons.ChevronDown}",
            Behaviour b => $"{EditorIcons.Wrench}  {b.Entity?.Name} / {Pretty(b.GetType())}  {EditorIcons.ChevronDown}",
            _ => $"{current.Name}  {EditorIcons.ChevronDown}",
        };

        var changed = false;
        if (ImGui.Button(label, new SysVec2(-1, 0)))
            ImGui.OpenPopup("##targetpick");

        if (ImGui.BeginPopup("##targetpick")) {
            if (ImGui.Selectable("None")) {
                EditorUndo.Push("Set Event Target");
                listener.TargetId = Guid.Empty;
                listener.MethodName = null;
                changed = true;
            }
            ImGui.Separator();

            foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
                ImGui.PushID(entity.InstanceId.GetHashCode());

                // The entity itself as a target (for Entity.SetActive etc.).
                if (ImGui.Selectable($"{EditorIcons.Package}  {entity.Name}")) {
                    SetTarget(listener, entity);
                    changed = true;
                }
                // Its components, indented.
                foreach (Behaviour behaviour in entity.Behaviours) {
                    if (ImGui.Selectable($"        {EditorIcons.Wrench}  {Pretty(behaviour.GetType())}##{behaviour.InstanceId}")) {
                        SetTarget(listener, behaviour);
                        changed = true;
                    }
                }
                ImGui.PopID();
            }
            ImGui.EndPopup();
        }
        return changed;
    }

    static void SetTarget(PersistentListener listener, BObject target) {
        EditorUndo.Push("Set Event Target");
        listener.TargetId = target.InstanceId;
        // Picking a new target invalidates the previous method binding.
        listener.MethodName = null;
        listener.Mode = PersistentListener.CallMode.Void;
        listener.StaticArgumentType = null;
        listener.StaticArgument = null;
    }

    // Method dropdown: lists the target's invokable methods. Each entry encodes the (method, mode)
    // it would bind — a 0-arg method binds Void; a 1-arg method binds Static (fixed arg) and, when
    // the event is generic and the param type matches, also offers a Dynamic entry.
    static bool DrawMethodPicker(PersistentListener listener, BObject target, Type dynamicType) {
        string current = listener.MethodName is null
            ? "(no method)"
            : $"{Pretty(listener.MethodName)}{ModeSuffix(listener)}";

        var changed = false;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##method", current))
            return false;

        foreach (MethodInfo method in BEventReflection.InvokableMethods(target.GetType())
                     .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) {
            ParameterInfo[] ps = method.GetParameters();

            if (ps.Length == 0) {
                if (ImGui.Selectable($"{Pretty(method.Name)} ()")) {
                    Bind(listener, method, PersistentListener.CallMode.Void, null);
                    changed = true;
                }
                continue;
            }

            Type paramType = ps[0].ParameterType;

            // Dynamic entry first when this event passes a compatible runtime value.
            if (dynamicType is not null && paramType.IsAssignableFrom(dynamicType)) {
                if (ImGui.Selectable($"{Pretty(method.Name)} (dynamic {Pretty(paramType)})")) {
                    Bind(listener, method, PersistentListener.CallMode.Dynamic, paramType);
                    changed = true;
                }
            }
            // Static (fixed-argument) entry.
            if (ImGui.Selectable($"{Pretty(method.Name)} ({Pretty(paramType)})")) {
                Bind(listener, method, PersistentListener.CallMode.Static, paramType);
                changed = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    static void Bind(PersistentListener listener, MethodInfo method, PersistentListener.CallMode mode, Type argType) {
        EditorUndo.Push("Set Event Method");
        listener.MethodName = method.Name;
        listener.Mode = mode;
        listener.StaticArgumentType = mode == PersistentListener.CallMode.Static ? argType : null;
        // Default the static argument so the widget has a value to edit (and to invoke with).
        listener.StaticArgument = mode == PersistentListener.CallMode.Static ? DefaultArg(argType) : null;
    }

    static string ModeSuffix(PersistentListener l) => l.Mode switch {
        PersistentListener.CallMode.Void => " ()",
        PersistentListener.CallMode.Dynamic => $" (dynamic {Pretty(l.StaticArgumentType)})",
        PersistentListener.CallMode.Static => $" ({Pretty(l.StaticArgumentType)})",
        _ => "",
    };

    // ---- static argument widget --------------------------------------------

    static bool DrawStaticArg(PersistentListener listener) {
        Type t = listener.StaticArgumentType;
        var changed = false;

        if (t == typeof(float)) {
            float v = listener.StaticArgument is float f ? f : 0f;
            if (ImGui.DragFloat("##arg", ref v, 0.05f)) { Commit(listener, v); changed = true; }
        }
        else if (t == typeof(int)) {
            int v = listener.StaticArgument is int n ? n : 0;
            if (ImGui.DragInt("##arg", ref v)) { Commit(listener, v); changed = true; }
        }
        else if (t == typeof(bool)) {
            bool v = listener.StaticArgument is bool b && b;
            if (ImGui.Checkbox("##arg", ref v)) { Commit(listener, v); changed = true; }
        }
        else if (t == typeof(string)) {
            string v = listener.StaticArgument as string ?? "";
            if (ImGui.InputText("##arg", ref v, 256)) { Commit(listener, v); changed = true; }
        }
        else if (t.IsEnum) {
            string[] names = Enum.GetNames(t);
            int idx = listener.StaticArgument is null ? 0 : Math.Max(0, Array.IndexOf(names, listener.StaticArgument.ToString()));
            if (ImGui.Combo("##arg", ref idx, names, names.Length)) { Commit(listener, Enum.Parse(t, names[idx])); changed = true; }
        }
        else if (typeof(BObject).IsAssignableFrom(t)) {
            changed = DrawAssetArg(listener, t);
        }
        else {
            ImGui.TextDisabled($"(unsupported arg: {Pretty(t)})");
        }
        return changed;
    }

    // Asset-typed static argument: a slot that accepts a drag-drop from the asset browser. Mirrors
    // the InspectorPanel asset slot but standalone (the listener stores the loaded asset directly).
    static bool DrawAssetArg(PersistentListener listener, Type assetType) {
        var asset = listener.StaticArgument as BObject;
        string display = "None";
        if (asset is not null && AssetDatabase.TryGetAssetGuid(asset, out Guid g))
            display = System.IO.Path.GetFileName(AssetDatabase.GuidToAssetPath(g) ?? asset.Name);

        ImGui.Button($"{EditorIcons.Document}  {display}", new SysVec2(-1, 0));
        if (!ImGui.BeginDragDropTarget())
            return false;

        var changed = false;
        unsafe {
            ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(AssetBrowserPanel.DragType);
            if (!payload.IsNull && payload.Data != null) {
                var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)payload.Data, payload.DataSize);
                var first = text?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (Guid.TryParse(first, out Guid dropped)) {
                    MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                        .MakeGenericMethod(assetType);
                    if (load.Invoke(null, [dropped]) is BObject loaded) {
                        Commit(listener, loaded);
                        changed = true;
                    }
                }
            }
        }
        ImGui.EndDragDropTarget();
        return changed;
    }

    static void Commit(PersistentListener listener, object value) {
        EditorUndo.Push("Set Event Argument");
        listener.StaticArgument = value;
    }

    static object DefaultArg(Type t) {
        if (t == typeof(string)) return "";
        if (typeof(BObject).IsAssignableFrom(t)) return null;
        return Activator.CreateInstance(t); // 0 / false / first enum value
    }

    static string Pretty(Type t) => t?.Name ?? "?";

    // "TakeDamage" -> "Take Damage" for menu readability.
    static string Pretty(string name) {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        sb.Append(name[0]);
        for (var i = 1; i < name.Length; i++) {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }
}
