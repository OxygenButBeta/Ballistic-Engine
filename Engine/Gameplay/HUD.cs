namespace BallisticEngine;

// Client-only presentation (plan §2). A SceneBehaviour (scene-wide, like Skybox), NEVER replicated —
// it's local UI. Phase 2 of init (§5) calls Init() once at StartPlay, AFTER the player exists, so the
// HUD can bind to the local PlayerController / PlayerState.
//
// P7: the binding is REAL. Init() runs after Phase 1's possession, so LocalController / LocalPlayerState
// resolve to the local player (Network.LocalPlayerController / LocalPlayerState — the owner-side controller
// + its PlayerState sibling). A game HUD overrides Init() and reads those to wire its widgets. The
// framework guarantees only the ORDERING (HUD.Init after possession); the binding itself is one line.
[Component("HUD", "Gameplay")]
public class HUD : SceneBehaviour {
    public static HUD Active { get; private set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    // The local player the HUD binds to (resolved when Init runs, in Phase 2 — possession already happened
    // in Phase 1). Cached on bind so a game HUD reads `Controller` / `PlayerState` directly. Null on a
    // dedicated server (no local player) or if no player was possessed.
    public PlayerController Controller { get; private set; }
    public PlayerState PlayerState { get; private set; }

    // Phase 2: client-only one-shot init. The framework binds the local player FIRST (so an override can
    // read Controller / PlayerState immediately), THEN calls the override. Runs ONCE at StartPlay.
    internal void RunInit() {
        Controller = Network.LocalPlayerController;
        PlayerState = Network.LocalPlayerState;
        Init();
    }

    // Override for HUD setup — bind to the local PlayerController / PlayerState (already resolved into the
    // Controller / PlayerState properties above; or read Network.LocalPlayer* directly). They exist by now
    // (Phase 1 ran). A late joiner re-binds via the §6 join flow.
    //   protected override void Init() {
    //       healthBar.Bind(Controller.Pawn);
    //       nameLabel.Text = PlayerState?.PlayerName;
    //   }
    protected virtual void Init() { }
}
