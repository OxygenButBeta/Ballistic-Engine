namespace BallisticEngine;

// Game-facing cursor control (Unity's Cursor.lockState / Cursor.visible). Locked hides the cursor
// and pins it to the window centre so first-person look can read raw Input.MouseDelta past the edge.
//
// INTENT model (single writer): a script sets `Cursor.Mode`/`Cursor.Locked` to express what it WANTS;
// that's stored, not slammed onto the window. Once per frame the HOST resolves intent onto the actual
// window via `Apply(allowed)` — the standalone player passes allowed=true (intent wins); the editor
// passes allowed = "Game tab focused" so the cursor is only ever grabbed over the Game view, never
// while you're working in the Scene view or panels. Because only the host writes the window, the
// script and host never fight (no Normal<->Grabbed flicker).
public static class Cursor
{
    // What the game wants. Scripts read/write this freely; it does NOT touch the window directly.
    public static CursorMode Mode { get; set; } = CursorMode.Normal;

    // Convenience matching Unity's bool: Locked when true, Normal when false.
    public static bool Locked {
        get => Mode == CursorMode.Locked;
        set => Mode = value ? CursorMode.Locked : CursorMode.Normal;
    }

    // Host-only: push the current intent onto the window, ONCE per frame. `allowed` is the host's veto
    // — when false (editor, Game view not the focused surface) the window is forced Normal regardless
    // of intent, so a script can never grab the cursor outside the Game view. Idempotent: only writes
    // when the window's state actually differs, so it's cheap to call every frame.
    public static void Apply(bool allowed) {
        if (Window.Current is null)
            return;
        CursorMode target = allowed ? Mode : CursorMode.Normal;
        if (Window.Current.CursorMode != target)
            Window.Current.CursorMode = target;
    }
}
