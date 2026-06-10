namespace BallisticEngine;

public class Behaviour : Component {
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

    // Fires when the component is attached to an entity, in BOTH edit and play mode. Use this for
    // editor-visible registration (e.g. adding a renderer to a draw set) — distinct from OnEnabled,
    // which is play-mode game logic. OnDetach fires when the component/entity is torn down.
    protected internal virtual void OnAttach() {
    }

    protected internal virtual void OnDetach() {
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