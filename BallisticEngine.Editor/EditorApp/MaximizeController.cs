namespace BallisticEngine.Editor;

// A1b (single-sourced maximize). The pure state behind "double-click a panel's tab to fill the
// window with it; Esc / double-click again / close it to restore." This replaces the old shadow-mode
// where the maximized target lived in a bare `string maximizedPanel` field hand-synced across THREE
// sites (the content re-route, the still-available hand-list, and the showXxx bools). The bug class it
// killed: a panel could get STUCK maximized ("açılınca kapanamayan") because one of the three sites
// forgot it. Here the state is ONE field + the two operations that can clear it, both can't-forget:
//   - Toggle(key): double-click toggles maximize on/off for that key (the second double-click restores).
//   - Clear():     Esc / the exit button / a stale-drop force-restores.
//   - DropIfUnavailable(isAvailable): every frame, if the maximized panel is no longer drawable (its
//     Window-menu toggle turned off, or its duplicated instance closed), the target is dropped so the
//     normal docked layout returns — there is NO state path that stays maximized on a gone panel.
//
// It is deliberately ImGui-free and dependency-free: the geometric tab hit-test + the actual draw stay
// in EditorApplication (they need ImGui), but the "which key is maximized, and can it ever get stuck"
// logic — the part that rotted — is isolated here so it's auditable and unit-checkable in one place.
internal sealed class MaximizeController {
    // The KEY (an EditorLayout.* dock name or a host-owned duplicate-instance label) that currently
    // fills the window, or null when nothing is maximized.
    public string Maximized { get; private set; }

    public bool IsMaximized => Maximized is not null;

    // Double-click semantics: maximize `key`, or restore if it was already the maximized one.
    public void Toggle(string key) => Maximized = Maximized == key ? null : key;

    // Force-restore (Esc, the exit-fullscreen button, or any explicit "get me out").
    public void Clear() => Maximized = null;

    // Per-frame stale-drop: if a panel is maximized but can no longer be shown fullscreen (closed /
    // its duplicate gone), restore the docked layout this frame. Returns true if it dropped one (so the
    // caller can skip the fullscreen path that same frame). No-op when nothing is maximized.
    public bool DropIfUnavailable(System.Func<string, bool> isAvailable) {
        if (Maximized is null) return false;
        if (isAvailable(Maximized)) return false;
        Maximized = null;
        return true;
    }
}
