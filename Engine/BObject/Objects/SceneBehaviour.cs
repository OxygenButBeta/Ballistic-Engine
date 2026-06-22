namespace BallisticEngine;

public abstract class SceneBehaviour : BObject {
    public bool IsEnabled { get; set; } = true;

    public bool IsActive => IsEnabled;

    internal int FaultStreak;
    internal string FaultCallback;

    protected internal virtual void OnAttach() {
    }

    protected internal virtual void OnDetach() {
    }

    public virtual void OnDrawGizmos(IGizmos gizmos) {
    }

    public virtual void OnDrawGizmosSelected(IGizmos gizmos) {
    }
}
