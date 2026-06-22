using System.Reflection;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class BEventEditor {
    static IEditorGui gui => EditorGui.Shared;

    public static bool Draw(string id, BEvent evt) {
        if (evt is null) {
            gui.TextDisabled("(null event)");
            return false;
        }

        gui.PushId(id);
        var changed = false;

        Type dynamicType = evt.DynamicArgType;
        string subtitle = dynamicType is null ? "Event" : $"Event<{Pretty(dynamicType)}>";
        gui.TextDisabled($"{subtitle} — {evt.PersistentListeners.Count} listener(s)");

        for (var i = 0; i < evt.PersistentListeners.Count; i++) {
            gui.PushId(i);
            PersistentListener listener = evt.PersistentListeners[i];

            if (DrawListenerCard(listener, dynamicType, out bool removeRequested))
                changed = true;

            if (removeRequested) {
                EditorUndo.Push("Remove Event Listener");
                evt.PersistentListeners.RemoveAt(i);
                i--;
                changed = true;
                gui.PopId();
                continue;
            }
            gui.PopId();
        }

        gui.Spacing();
        if (gui.Button($"{EditorIcons.Add}  Add Listener", new SysVec2(-1, 0))) {
            EditorUndo.Push("Add Event Listener");
            evt.PersistentListeners.Add(new PersistentListener());
            changed = true;
        }

        gui.PopId();
        return changed;
    }

    static bool DrawListenerCard(PersistentListener listener, Type dynamicType, out bool removeRequested) {
        removeRequested = false;
        var changed = false;

        gui.PushFramePadding(new SysVec2(6, 3));
        gui.BeginChildAutoResizeY("card", border: true);

        float removeW = gui.FrameHeight;
        gui.SetNextItemWidth(gui.ContentRegionAvail.X - removeW - 6);
        if (DrawTargetPicker(listener))
            changed = true;
        gui.SameLine();
        if (EditorIcons.GhostButton("rm", EditorIcons.Delete, "Remove listener", removeW))
            removeRequested = true;

        BObject target = listener.ResolveTarget();
        if (target is null) {
            gui.TextDisabled(listener.TargetId == Guid.Empty
                ? "Pick a target to choose a method."
                : "Target not found in this scene.");
        }
        else {
            if (DrawMethodPicker(listener, target, dynamicType))
                changed = true;

            if (listener.Mode == PersistentListener.CallMode.Static && listener.StaticArgumentType is not null) {
                gui.SetNextItemWidth(-1);
                if (DrawStaticArg(listener))
                    changed = true;
            }
        }

        gui.EndChild();
        gui.PopStyleVar();
        return changed;
    }

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
        if (gui.Button(label, new SysVec2(-1, 0)))
            gui.OpenPopup("##targetpick");

        if (AcceptEntityDrop(out Entity dropped)) {
            SetTarget(listener, dropped);
            changed = true;
        }

        if (gui.BeginPopup("##targetpick")) {
            if (gui.IsWindowAppearing()) { targetSearch = ""; gui.SetKeyboardFocusHere(); }
            gui.SetNextItemWidth(240);
            gui.InputTextWithHint("##targetsearch", $"{EditorIcons.Search} Search...", ref targetSearch, 64);
            gui.Separator();

            if (gui.Selectable("None")) {
                EditorUndo.Push("Set Event Target");
                listener.TargetId = Guid.Empty;
                listener.MethodName = null;
                changed = true;
            }
            gui.Separator();

            gui.BeginChild("##targetlist", new SysVec2(240, 320), border: false);
            bool searching = targetSearch.Length > 0;
            bool Match(string s) => !searching || s.Contains(targetSearch, StringComparison.OrdinalIgnoreCase);
            foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
                gui.PushId(entity.InstanceId.GetHashCode());

                if (Match(entity.Name ?? "") && gui.Selectable($"{EditorIcons.Package}  {entity.Name}")) {
                    SetTarget(listener, entity);
                    changed = true;
                }

                foreach (Behaviour behaviour in entity.Behaviours) {
                    string comp = Pretty(behaviour.GetType());
                    if ((Match(comp) || Match($"{entity.Name}/{comp}")) &&
                        gui.Selectable($"        {EditorIcons.Wrench}  {comp}##{behaviour.InstanceId}")) {
                        SetTarget(listener, behaviour);
                        changed = true;
                    }
                }
                gui.PopId();
            }
            gui.EndChild();
            gui.EndPopup();
        }
        return changed;
    }

    static string targetSearch = "";

    static bool AcceptEntityDrop(out Entity entity) {
        entity = null;
        if (!gui.BeginDragDropTarget())
            return false;
        if (gui.AcceptDragDropPayloadInt("BALLISTIC_ENTITY") is { } hash) {
            foreach (Entity e in SceneManager.GetCurrentScene().Entities)
                if (e.InstanceId.GetHashCode() == hash) { entity = e; break; }
        }
        gui.EndDragDropTarget();
        return entity is not null;
    }

    static void SetTarget(PersistentListener listener, BObject target) {
        EditorUndo.Push("Set Event Target");
        listener.TargetId = target.InstanceId;
        listener.MethodName = null;
        listener.Mode = PersistentListener.CallMode.Void;
        listener.StaticArgumentType = null;
        listener.StaticArgument = null;
    }

    static bool DrawMethodPicker(PersistentListener listener, BObject target, Type dynamicType) {
        string current = listener.MethodName is null
            ? "(no method)"
            : $"{Pretty(listener.MethodName)}{ModeSuffix(listener)}";

        var changed = false;
        gui.SetNextItemWidth(-1);
        if (!gui.BeginCombo("##method", current))
            return false;

        foreach (MethodInfo method in BEventReflection.InvokableMethods(target.GetType())
                     .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) {
            ParameterInfo[] ps = method.GetParameters();

            if (ps.Length == 0) {
                if (gui.Selectable($"{Pretty(method.Name)} ()")) {
                    Bind(listener, method, PersistentListener.CallMode.Void, null);
                    changed = true;
                }
                continue;
            }

            Type paramType = ps[0].ParameterType;

            if (dynamicType is not null && paramType.IsAssignableFrom(dynamicType)) {
                if (gui.Selectable($"{Pretty(method.Name)} (dynamic {Pretty(paramType)})")) {
                    Bind(listener, method, PersistentListener.CallMode.Dynamic, paramType);
                    changed = true;
                }
            }

            if (gui.Selectable($"{Pretty(method.Name)} ({Pretty(paramType)})")) {
                Bind(listener, method, PersistentListener.CallMode.Static, paramType);
                changed = true;
            }
        }

        gui.EndCombo();
        return changed;
    }

    static void Bind(PersistentListener listener, MethodInfo method, PersistentListener.CallMode mode, Type argType) {
        EditorUndo.Push("Set Event Method");
        listener.MethodName = method.Name;
        listener.Mode = mode;
        listener.StaticArgumentType = mode == PersistentListener.CallMode.Static ? argType : null;
        listener.StaticArgument = mode == PersistentListener.CallMode.Static ? DefaultArg(argType) : null;
    }

    static string ModeSuffix(PersistentListener l) => l.Mode switch {
        PersistentListener.CallMode.Void => " ()",
        PersistentListener.CallMode.Dynamic => $" (dynamic {Pretty(l.StaticArgumentType)})",
        PersistentListener.CallMode.Static => $" ({Pretty(l.StaticArgumentType)})",
        _ => "",
    };

    static bool DrawStaticArg(PersistentListener listener) {
        Type t = listener.StaticArgumentType;
        var changed = false;

        if (t == typeof(float)) {
            float v = listener.StaticArgument is float f ? f : 0f;
            if (gui.DragFloat("##arg", ref v, 0.05f)) { Commit(listener, v); changed = true; }
        }
        else if (t == typeof(int)) {
            int v = listener.StaticArgument is int n ? n : 0;
            if (gui.DragInt("##arg", ref v)) { Commit(listener, v); changed = true; }
        }
        else if (t == typeof(bool)) {
            bool v = listener.StaticArgument is bool b && b;
            if (gui.Checkbox("##arg", ref v)) { Commit(listener, v); changed = true; }
        }
        else if (t == typeof(string)) {
            string v = listener.StaticArgument as string ?? "";
            if (gui.InputText("##arg", ref v, 256)) { Commit(listener, v); changed = true; }
        }
        else if (t.IsEnum) {
            string[] names = Enum.GetNames(t);
            int idx = listener.StaticArgument is null ? 0 : Math.Max(0, Array.IndexOf(names, listener.StaticArgument.ToString()));
            if (gui.Combo("##arg", ref idx, names)) { Commit(listener, Enum.Parse(t, names[idx])); changed = true; }
        }
        else if (typeof(BObject).IsAssignableFrom(t)) {
            changed = DrawAssetArg(listener, t);
        }
        else {
            gui.TextDisabled($"(unsupported arg: {Pretty(t)})");
        }
        return changed;
    }

    static bool DrawAssetArg(PersistentListener listener, Type assetType) {
        var asset = listener.StaticArgument as BObject;
        string display = "None";
        if (asset is not null && AssetDatabase.TryGetAssetGuid(asset, out Guid g))
            display = System.IO.Path.GetFileName(AssetDatabase.GuidToAssetPath(g) ?? asset.Name);

        gui.Button($"{EditorIcons.Document}  {display}", new SysVec2(-1, 0));
        if (!gui.BeginDragDropTarget())
            return false;

        var changed = false;
        string text = gui.AcceptDragDropPayloadString(AssetBrowserPanel.DragType);
        if (text is not null) {
            var first = text.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (Guid.TryParse(first, out Guid dropped)) {
                MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                    .MakeGenericMethod(assetType);
                if (load.Invoke(null, [dropped]) is BObject loaded) {
                    Commit(listener, loaded);
                    changed = true;
                }
            }
        }
        gui.EndDragDropTarget();
        return changed;
    }

    static void Commit(PersistentListener listener, object value) {
        EditorUndo.Push("Set Event Argument");
        listener.StaticArgument = value;
    }

    static object DefaultArg(Type t) {
        if (t == typeof(string)) return "";
        if (typeof(BObject).IsAssignableFrom(t)) return null;
        return Activator.CreateInstance(t);
    }

    static string Pretty(Type t) => t?.Name ?? "?";

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
