namespace BallisticEngine;

// A possessable actor (plan §2 / §6) — the thing a PlayerController drives. A NetworkBehaviour, so it
// replicates and the editor/registry discover it. Hand-place one in a scene (D4 mode B → possessed) or
// let GameMode spawn DefaultPawn (D4 mode A). CharacterPawn (predicted movement) is a later phase.
//
// P0 = possession wiring + the role-gated hooks. Movement/prediction is P5.
[Component("Pawn", "Gameplay")]
public class Pawn : NetworkBehaviour {
    // The controller possessing this pawn, or null when unpossessed. Set by PlayerController.Possess
    // (server-authoritative). NotSerialized: a runtime link, never persisted.
    [NotSerialized]
    public PlayerController Controller { get; internal set; }

    // True once a GameMode has claimed this scene-placed pawn for a connection (so a second player
    // doesn't claim the same one — §6 deterministic claim). Runtime-only.
    [NotSerialized]
    public bool IsClaimed { get; internal set; }

    public bool IsPossessed => Controller is not null;

    // ---- possession hooks (auto-targeted on the owning machine; plan §4e) -------------------------
    // OnPossessed fires on the machine that controls this pawn (the input authority) right after
    // Possess; OnUnpossessed on release. The framework targets them — the body writes no IsOwner gate.
    protected internal virtual void OnPossessed(PlayerController controller) { }
    protected internal virtual void OnUnpossessed() { }

    // Called by PlayerController.Possess / Unpossess — drives the hooks with ScriptGuard firewalling.
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
