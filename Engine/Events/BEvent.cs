using System.Reflection;

namespace BallisticEngine;

public class BEvent {
    public readonly List<PersistentListener> PersistentListeners = new();

    readonly List<Action> runtimeListeners = new();

    public void AddListener(Action listener) {
        if (listener is not null)
            runtimeListeners.Add(listener);
    }

    public void RemoveListener(Action listener) => runtimeListeners.Remove(listener);

    public void RemoveAllListeners() => runtimeListeners.Clear();

    public void Invoke() {
        foreach (PersistentListener listener in PersistentListeners)
            listener.Invoke(this, dynamicArg: null, hasDynamicArg: false);
        InvokeRuntimeListeners();
    }

    protected void InvokeRuntimeListeners() {
        foreach (Action listener in runtimeListeners.ToArray()) {
            try { listener(); }
            catch (Exception e) { Debugging.LogError($"BEvent listener threw:\n{e}"); }
        }
    }

    public virtual Type DynamicArgType => null;
}


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

        InvokeRuntimeListeners();
    }

    public override Type DynamicArgType => typeof(T);
}
