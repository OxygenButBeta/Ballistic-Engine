namespace BallisticEngine;

[Component("Pawn", "Gameplay")]
public class Pawn : NetworkBehaviour {
    [NotSerialized]
    public PlayerController Controller { get; internal set; }

    [NotSerialized]
    public bool IsClaimed { get; internal set; }

    public bool IsPossessed => Controller is not null;

    protected internal virtual void OnPossessed(PlayerController controller) { }
    protected internal virtual void OnUnpossessed() { }

    internal void FirePossessed(PlayerController controller) {
        Controller = controller;
        try { OnPossessed(controller); }
        catch (Exception e) { ScriptGuard.Report(this, "OnPossessed", e); }
    }

    internal void FireUnpossessed() {
        try { OnUnpossessed(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnUnpossessed", e); }
        Controller = null;
        IsClaimed = false;
    }
}
