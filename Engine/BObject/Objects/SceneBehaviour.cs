namespace BallisticEngine;

// A scene-wide component: lives on the Scene itself rather than on an entity, and shows up in
// the editor's "Scene" hierarchy instead of the entity hierarchy. Use for things that configure
// the whole scene (skybox, fog, post-process volumes, ...). First user: Skybox.
//
// No Tick for now — scene behaviours are configuration carriers; systems read them directly.
public abstract class SceneBehaviour : BObject {
    public bool IsEnabled { get; set; } = true;

    public bool IsActive => IsEnabled;

    // ScriptGuard bookkeeping (see Behaviour.FaultStreak) — gizmo callbacks run per frame.
    internal int FaultStreak;
    internal string FaultCallback;

    // Fired when added to / removed from a scene (edit and play mode alike).
    protected internal virtual void OnAttach() {
    }

    protected internal virtual void OnDetach() {
    }

    // Editor scene-view handles, mirroring Behaviour's hooks: OnDrawGizmos runs for every
    // active scene behaviour, OnDrawGizmosSelected when picked in the hierarchy's Scene tab.
    public virtual void OnDrawGizmos(IGizmos gizmos) {
    }

    public virtual void OnDrawGizmosSelected(IGizmos gizmos) {
    }
}
