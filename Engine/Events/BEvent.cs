using System.Reflection;

namespace BallisticEngine;

// Serialized event, the Ballistic equivalent of Unity's UnityEvent. A component exposes a public
// BEvent member; in the inspector you wire up "persistent listeners" — pick a target (an Entity or
// any Behaviour on it), pick one of its public methods, and (optionally) a static argument. The
// wiring is saved into the .scene file by InstanceId, so it survives reload/undo like an asset ref.
//
// Two flavours:
//   BEvent           — fire-and-forget; listeners are void methods taking 0 args or 1 static arg.
//   BEvent<T>        — carries a runtime value of type T; a listener can be set to "dynamic" mode
//                      so the invoked value flows through to a method(T) parameter (Unity's dynamic
//                      listener), or it can still take a fixed static argument instead.
//
// Listeners added in code via AddListener(Action) are runtime-only (NOT serialized) — same split as
// Unity's RemoveListener/persistent calls. Invoke() runs persistent listeners first, then runtime
// ones, each firewalled by ScriptGuard so a bad listener can't take the frame down.
//
// Serialization/inspector live in the editor + SceneSerializer; this type is engine-layer (BCL +
// reflection only) so components in any assembly can declare events.
public class BEvent {
    // Authored in the editor, serialized to the scene. Public so the serializer and inspector
    // (both outside this class) can read/rebuild the list; game code should use Add/RemoveListener.
    public readonly List<PersistentListener> PersistentListeners = new();

    readonly List<Action> runtimeListeners = new();

    // Runtime subscription (Unity's AddListener). Not serialized; cleared on RemoveAllListeners.
    public void AddListener(Action listener) {
        if (listener is not null)
            runtimeListeners.Add(listener);
    }

    public void RemoveListener(Action listener) => runtimeListeners.Remove(listener);

    // Drops only the code-added listeners, matching Unity (persistent/authored ones stay).
    public void RemoveAllListeners() => runtimeListeners.Clear();

    public void Invoke() {
        foreach (PersistentListener listener in PersistentListeners)
            listener.Invoke(this, dynamicArg: null, hasDynamicArg: false);
        InvokeRuntimeListeners();
    }

    // Runs only the code-added parameterless listeners (not the persistent ones). Called by the base
    // Invoke() and by BEvent<T>.Invoke(value) — which has already run the persistent + typed
    // listeners and just needs the plain Action subscribers to fire too.
    protected void InvokeRuntimeListeners() {
        // Snapshot: a listener may add/remove listeners while firing.
        foreach (Action listener in runtimeListeners.ToArray()) {
            try { listener(); }
            catch (Exception e) { Debugging.LogError($"BEvent listener threw:\n{e}"); }
        }
    }

    // The argument TYPE a dynamic listener receives — none for the non-generic BEvent. Used by the
    // inspector to decide which authored methods qualify for "dynamic" mode. BEvent<T> overrides it.
    public virtual Type DynamicArgType => null;
}

// Typed event carrying a runtime value. Invoke(value) passes `value` to dynamic-mode listeners and
// to code listeners registered as Action<T>; static-mode and 0-arg listeners ignore it.
public class BEvent<T> : BEvent {
    readonly List<Action<T>> typedRuntimeListeners = new();

    public void AddListener(Action<T> listener) {
        if (listener is not null)
            typedRuntimeListeners.Add(listener);
    }

    public void RemoveListener(Action<T> listener) => typedRuntimeListeners.Remove(listener);

    public void Invoke(T value) {
        foreach (PersistentListener listener in PersistentListeners)
            listener.Invoke(this, dynamicArg: value, hasDynamicArg: true);

        foreach (Action<T> listener in typedRuntimeListeners.ToArray()) {
            try { listener(value); }
            catch (Exception e) { Debugging.LogError($"BEvent<{typeof(T).Name}> listener threw:\n{e}"); }
        }

        // Plain parameterless Action subscribers fire too (the persistent calls already ran above).
        InvokeRuntimeListeners();
    }

    public override Type DynamicArgType => typeof(T);
}

// One authored call: "on this event, call <Method> on <Target> with <Mode>". Serialized by the
// scene serializer; the inspector edits it. Target resolution is deferred — TargetId is stored, the
// live Target is resolved lazily (and cached) at Invoke time, so a listener pointing at an entity
// loaded later in the same scene still binds.
public sealed class PersistentListener {
    public enum CallMode { Void, Static, Dynamic }

    // InstanceId of the target Entity or Behaviour. Resolved against the current scene each time the
    // cache misses (cleared on scene reload by identity change — the resolved object is verified).
    public Guid TargetId;

    public string MethodName;
    public CallMode Mode = CallMode.Void;

    // The persistent static argument (Mode == Static). Held as a boxed value already coerced to the
    // method's parameter type, or an AssetRef-resolved BObject. Null for Void/Dynamic.
    public object StaticArgument;

    // The declared type of the static argument, so the inspector renders the right widget and the
    // serializer round-trips it even when StaticArgument is null/default.
    public Type StaticArgumentType;

    BObject resolvedTarget;

    public BObject ResolveTarget() {
        if (resolvedTarget is not null && resolvedTarget.InstanceId == TargetId)
            return resolvedTarget;
        resolvedTarget = SceneManager.FindByInstanceId(TargetId);
        return resolvedTarget;
    }

    internal void Invoke(BEvent source, object dynamicArg, bool hasDynamicArg) {
        BObject target = ResolveTarget();
        if (target is null)
            return; // target was deleted / not in this scene — silently skip, like a Unity missing ref

        MethodInfo method = FindMethod(target.GetType());
        if (method is null) {
            Debugging.LogWarning(
                $"BEvent: target '{target.Name}' has no method '{MethodName}' matching mode {Mode}; skipped.");
            return;
        }

        object[] args = Mode switch {
            CallMode.Static  => [StaticArgument],
            CallMode.Dynamic => hasDynamicArg ? [dynamicArg] : null,
            _                => null,
        };
        if (Mode == CallMode.Dynamic && !hasDynamicArg)
            return; // dynamic listener on a non-generic invoke — nothing to pass

        try { method.Invoke(target, args); }
        catch (TargetInvocationException tie) {
            Debugging.LogError($"BEvent: {target.GetType().Name}.{MethodName} threw:\n{tie.InnerException}");
        }
        catch (Exception e) {
            Debugging.LogError($"BEvent: failed to invoke {target.GetType().Name}.{MethodName}:\n{e}");
        }
    }

    // The method matching this listener's name + mode (0 params for Void; 1 matching param otherwise).
    MethodInfo FindMethod(Type targetType) {
        foreach (MethodInfo m in BEventReflection.InvokableMethods(targetType)) {
            if (m.Name != MethodName)
                continue;
            ParameterInfo[] ps = m.GetParameters();
            switch (Mode) {
                case CallMode.Void when ps.Length == 0:
                    return m;
                case CallMode.Static when ps.Length == 1 && (StaticArgumentType is null ||
                    ps[0].ParameterType.IsAssignableFrom(StaticArgumentType)):
                    return m;
                case CallMode.Dynamic when ps.Length == 1:
                    return m;
            }
        }
        return null;
    }
}
