namespace BallisticEngine;

// Possesses a Pawn and owns the input event source (plan §2 / §6 / §7). A NetworkBehaviour: it
// replicates to owner+server only. The possession pair with Pawn.
//
// The input contract (D3, the Grade-1 footgun kill): SetupInput is called by the framework on the
// INPUT AUTHORITY ONLY (via OnStartLocalPlayer, owner-gated in NetworkBehaviour.DriveNetSpawn). On a
// proxy it is NEVER called — the subclass body has zero `if (IsLocalPlayer)`, and a proxy's input
// events simply never exist. There is no false-branch to misuse.
[Component("Player Controller", "Gameplay")]
public class PlayerController : NetworkBehaviour {
    // The pawn this controller drives, or null. Set by Possess (server-authoritative).
    [NotSerialized]
    public Pawn Pawn { get; private set; }

    // The owner-local input source. Created on the input authority when input is set up; null on a
    // proxy (no input pipeline is built there). Game code reaches it only inside SetupInput.
    [NotSerialized]
    public InputComponent Input { get; private set; }

    // ---- possession (server-authoritative; plan §6) -----------------------------------------------
    // Wire pawn <-> controller. On the machine that locally controls the pawn (the input authority),
    // SetupInput fires via OnStartLocalPlayer; on every other machine the pawn is a proxy and SetupInput
    // is never called (Unreal's IsLocallyControlled, zero gate code). Possess is called by the phase
    // runner (Phase 1) and by GameMode for joins.
    public void Possess(Pawn pawn) {
        if (pawn is null)
            return;
        if (Pawn is not null)
            Unpossess();
        Pawn = pawn;
        pawn.FirePossessed(this);

        // If the controller's net strand already ran (OnStartLocalPlayer fired) we set input up now;
        // otherwise OnStartLocalPlayer will (it calls TrySetupInput). Either order lands once.
        if (IsOwner)
            TrySetupInput();
    }

    public void Unpossess() {
        if (Pawn is null)
            return;
        Pawn p = Pawn;
        Pawn = null;
        p.FireUnpossessed();
    }

    // OnStartLocalPlayer fires ONLY on the input authority (NetworkBehaviour.DriveNetSpawn gates it on
    // IsOwner) — so this is the owner-only seam. Build the input source and call SetupInput.
    protected internal override void OnStartLocalPlayer() => TrySetupInput();

    void TrySetupInput() {
        if (Input is not null)
            return;   // already set up (Possess + OnStartLocalPlayer both reach here; idempotent)
        Input = new InputComponent();
        try { SetupInput(Input); }
        catch (Exception e) { ScriptGuard.Report(this, "SetupInput", e); }
    }

    // Override to bind input events (plan §7). Called on the OWNER only — no gate. The InputComponent
    // routes events through the owner-routed surface; on a proxy this never runs.
    //   protected override void SetupInput(InputComponent input) {
    //       input.OnAxis2(PlayerActions.Move, v => Pawn.AddMoveInput(v));
    //       input.OnAction(PlayerActions.Jump, Pawn.Jump);
    //   }
    protected virtual void SetupInput(InputComponent input) { }

    // Sample the bound input each frame on the owner (the polling-to-events bridge — the events are
    // driven from the per-frame sample). Tick is owner-local; on a proxy Input is null so nothing runs.
    protected internal override void Tick(in float delta) {
        Input?.Sample(in delta);
    }
}
