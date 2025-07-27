namespace BallisticEngine;

public class Behaviour : Component {
    public bool isEnabled = true;
    public bool IsActive => entity.IsActive && isEnabled;


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