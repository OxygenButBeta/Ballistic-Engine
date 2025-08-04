namespace BallisticEngine;

public class Behaviour : Component {
    protected IInputProvider Input => BEngineEntry.Input;
    protected IEngineTimer Time => BEngineEntry.Time;

    public bool IsEnabled {
        get => isEnabled;
        set {
            if (isEnabled == value) return;
            isEnabled = value;
            if (IsActive) {
                OnEnabled();
            }
            else {
                OnDisabled();
            }
        }
    }

    bool isEnabled = true;
    public bool IsActive => entity.IsActive && IsEnabled;
    public Transform transform => entity.transform;


    protected internal virtual void OnBegin() {
    }

    protected virtual void OnEnd() {
    }

    protected internal virtual void OnEnabled() {
    }

    protected internal virtual void OnDisabled() {
    }

    protected internal virtual void Tick(in float delta) {
    }

    protected internal virtual void FixedTick(in float delta) {
    }
}