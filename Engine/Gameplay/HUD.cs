namespace BallisticEngine;

// Client-only presentation (plan §2). A SceneBehaviour (scene-wide, like Skybox), NEVER replicated —
// it's local UI. Phase 2 of init (§5) calls Init() once at StartPlay, AFTER the player exists, so the
// HUD can bind to the local PlayerController / PlayerState.
//
// P0 = the type + the Init seam. Binding to UI widgets is game code; the framework just guarantees the
// ordering (HUD.Init runs after Phase 1's possession).
[Component("HUD", "Gameplay")]
public class HUD : SceneBehaviour {
    public static HUD Active { get; private set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    // Phase 2: client-only one-shot init. Runs ONCE at StartPlay; bind to the local
    // PlayerController/PlayerState here (they exist by now — Phase 1 ran). Override for HUD setup.
    public virtual void Init() { }
}
