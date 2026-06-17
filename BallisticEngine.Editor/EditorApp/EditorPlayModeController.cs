namespace BallisticEngine.Editor;

// A5 (play/edit mode controller — the LAST Phase-A item). The EDITOR-SIDE wrapper around the engine's
// play lifecycle (SceneManager.StartPlay/StopPlay, which already own the pre-play YAML snapshot + the
// Stop-restores-the-edit-scene contract). Before A5, the mode TRANSITION side-effects lived inline in
// the toolbar's Play/Stop button bodies:
//   - enter play: persist edits to disk first (so a crash mid-play can't lose them), then StartPlay,
//                 then focus the Game view.
//   - exit play:  StopPlay, then clear any leftover cursor-lock intent from the play session, clear the
//                 selection, then focus the Scene view.
// Scattered inline in the draw code, those side-effects were easy to get out of sync (the exact
// "side-effects inline in toolbar-draw, no state machine" the plan flagged, §3.1 / §PHASE A A5). Here
// they become EXPLICIT enter/exit-transition handlers in one place, so a transition is one call:
//   - EnterPlay(): runs the enter-transition (save-guard) -> SceneManager.StartPlay() -> OnEntered hook.
//   - ExitPlay():  SceneManager.StopPlay() -> the exit-transition (cursor reset, selection clear) -> OnExited hook.
//
// It is deliberately ImGui-free: the toolbar still owns the buttons + the play-blocked tooltip (they
// need ImGui); the controller owns only the TRANSITION logic — the part that was scattered. The actual
// editor-specific effects (which window to focus, how to save, how to clear the selection) are supplied
// as handlers at construction, exactly like EditorInputRouter carries opaque Action bodies and
// MaximizeController carries Func predicates — so the controller stays decoupled + auditable in one spot.
internal sealed class EditorPlayModeController {
    // The enter-guard: persist unsaved edits to disk BEFORE play (Unity-style). Play mode only keeps an
    // in-memory snapshot that Stop restores, so a close/crash mid-play would otherwise lose unsaved
    // edits (collider sizes, etc.). Runs before StartPlay. May be a no-op (nothing dirty / no file).
    readonly System.Action saveBeforePlay;
    // Editor effect fired AFTER the engine entered play (focus the Game view).
    readonly System.Action onEntered;
    // Editor effect fired AFTER the engine left play (clear leftover cursor-lock intent, clear the
    // selection, focus the Scene view).
    readonly System.Action onExited;

    public EditorPlayModeController(System.Action saveBeforePlay, System.Action onEntered, System.Action onExited) {
        this.saveBeforePlay = saveBeforePlay ?? throw new System.ArgumentNullException(nameof(saveBeforePlay));
        this.onEntered = onEntered ?? throw new System.ArgumentNullException(nameof(onEntered));
        this.onExited = onExited ?? throw new System.ArgumentNullException(nameof(onExited));
    }

    public bool IsPlaying => SceneManager.IsPlaying;

    // The reason play is currently blocked (a failed script compile), or null when play is allowed.
    // The toolbar uses this to disable + tooltip the Play button; StartPlay also self-guards on it.
    public string BlockedReason => SceneManager.PlayBlocked?.Invoke();

    public bool CanEnterPlay => !SceneManager.IsPlaying && BlockedReason is null;

    // Enter play: the explicit enter-transition. No-ops if already playing or blocked (so a double
    // click / a stale call can't double-enter). Order: save-guard -> engine StartPlay -> editor effect.
    // Returns true if it actually entered.
    public bool EnterPlay() {
        if (SceneManager.IsPlaying)
            return false;
        if (BlockedReason is not null)
            return false;
        saveBeforePlay();
        SceneManager.StartPlay();
        onEntered();
        return true;
    }

    // Exit play: the explicit exit-transition. No-ops if not playing. Order: engine StopPlay -> editor
    // effect (cursor reset / selection clear / focus). Returns true if it actually exited.
    public bool ExitPlay() {
        if (!SceneManager.IsPlaying)
            return false;
        SceneManager.StopPlay();
        onExited();
        return true;
    }
}
