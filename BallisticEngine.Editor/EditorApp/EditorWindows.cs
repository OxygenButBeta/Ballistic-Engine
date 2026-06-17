namespace BallisticEngine.Editor;

// The static window facade the self-registered [MenuItem] methods call into (editor-rework Rule 3 / A1).
//
// Unity exposes `EditorWindow.GetWindow<T>()` as a static entry point so a `[MenuItem]` method can open a
// window without holding an instance. We mirror that: every editor window carries a static, parameterless
// [MenuItem("Window/Xxx")] method (discovered by EditorWindowRegistry), and that method routes here —
//
//   [MenuItem("Window/Inspector")] static void Open() => EditorWindows.Toggle(EditorLayout.Inspector);
//
// so the menu method stays static + reflection-discoverable while the ACTUAL open/toggle acts on the live
// EditorApplication. EditorApplication installs the handlers below in its ctor (`EditorWindows.Bind(...)`);
// the registry queries `IsOpen` to render Unity-style checkmarks. The result is the Rule-3 invariant:
// EditorApplication no longer hand-lists any window in the menu bar — the menu is BUILT from the discovered
// [MenuItem]s, and each window's open/close goes through one keyed facade instead of a per-window bool field
// referenced by name at the call site.
//
// Window KEYS are the EditorLayout.* dock names for the dockable panels (so Open() can route to the
// DockPanelHost backing store), plus a handful of standalone-window keys (Settings/Profiler/...) the facade
// maps to their owning panel's Open flag. Keeping the keys = the dock names means the default-layout builder
// and the registry name the same windows (placement ≠ ownership — the window still self-registers).
internal static class EditorWindows {
    // Toggle a dockable/standalone window's visibility (the Window-menu checkbox behaviour). For a closed
    // window it (re)opens + focuses it; for an open one it closes it.
    public static Action<string> ToggleHandler;
    // Open ANOTHER instance of a dockable kind (the "Add Panel" behaviour) — always spawns/focuses, never closes.
    public static Action<string> OpenHandler;
    // Whether a window is currently shown (drives the menu checkmark). Defaults to false if unbound.
    public static Func<string, bool> IsOpenHandler;
    // Whether a window's menu entry should be enabled (e.g. Save is disabled while playing). Defaults to true.
    public static Func<string, bool> IsEnabledHandler;

    // EditorApplication wires the real implementations once, in its ctor. Until then the facade no-ops so a
    // headless harness can discover [MenuItem]s and invoke them without a live editor (they just route to nothing).
    public static void Bind(Action<string> toggle, Action<string> open, Func<string, bool> isOpen,
        Func<string, bool> isEnabled) {
        ToggleHandler = toggle;
        OpenHandler = open;
        IsOpenHandler = isOpen;
        IsEnabledHandler = isEnabled;
    }

    public static void Toggle(string key) => ToggleHandler?.Invoke(key);
    public static void Open(string key) => OpenHandler?.Invoke(key);
    public static bool IsOpen(string key) => IsOpenHandler?.Invoke(key) ?? false;
    public static bool IsEnabled(string key) => IsEnabledHandler?.Invoke(key) ?? true;
}
