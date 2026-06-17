namespace BallisticEngine.Editor;

// The editor's self-registered window menu commands (editor-rework Rule 3 / A1). Each window's "open me"
// entry lives HERE as a static [MenuItem] method instead of being hand-listed inside EditorApplication's
// DrawMainMenuBar. EditorWindowRegistry discovers these by reflection (TypeCache scan) and the menu bar is
// built from them — so EditorApplication no longer references any window by name when drawing the menu.
//
// Each method just routes to the EditorWindows facade, which EditorApplication binds to the real toggle/open
// logic at startup. The methods are intentionally trivial: the [MenuItem] PATH is the declaration of where
// the window appears, the facade key is which window it controls. Keeping them in one cohesive file (rather
// than scattering one onto each panel class) is a deliberate A1 choice — the panels are plain ImGui draw
// helpers with no shared base, and centralizing the menu surface here keeps the keys and the path→state map
// in one place. (A later chunk can move a method next to its window once windows gain a common base.)
//
// WHY a path→key map: a plain [MenuItem] is a fire-once command (Unity needs a separate validate function for
// a checkmark). To preserve the current Window-menu UX (a checkmark mirroring each window's open state) the
// menu renderer needs the window KEY behind a menu PATH. The engine-side [MenuItem] attribute is deliberately
// key-agnostic (zero editor refs), so the editor-side mapping lives here next to the methods that define it.
internal static class EditorMenus {
    // Maps a discovered [MenuItem] path → the EditorWindows key whose open-state drives its checkmark.
    // A path absent here renders as a plain (checkmark-less) command. Built from the methods below.
    public static readonly IReadOnlyDictionary<string, string> PathToWindowKey = new Dictionary<string, string> {
        ["Window/Entities"] = EditorLayout.Entities,
        ["Window/Scene Components"] = EditorLayout.SceneComponents,
        ["Window/Inspector"] = EditorLayout.Inspector,
        ["Window/Assets"] = EditorLayout.Assets,
        ["Window/Console"] = EditorLayout.Console,
        ["Window/Statistics"] = WindowKeys.Statistics,
        ["Window/Profiler"] = WindowKeys.Profiler,
        ["Window/Build"] = WindowKeys.Build,
        ["Window/Tags & Layers"] = WindowKeys.TagsLayers,
        ["Window/Settings"] = WindowKeys.Settings,
    };

    // Keys for the standalone (non-dockable) windows the facade toggles via their owning panel's Open flag.
    // The dockable panels use their EditorLayout.* dock names as keys (so Open() can reach DockPanelHost).
    public static class WindowKeys {
        public const string Statistics = "##win.statistics";
        public const string Profiler = "##win.profiler";
        public const string Build = "##win.build";
        public const string TagsLayers = "##win.tagslayers";
        public const string Settings = "##win.settings";
        public const string UnityImport = "##win.unityimport";
    }

    // ── Window menu — the five core dockable panels (Order groups them above the standalone tools) ──────
    [MenuItem("Window/Entities", 0)] static void Entities() => EditorWindows.Toggle(EditorLayout.Entities);
    [MenuItem("Window/Scene Components", 1)] static void SceneComponents() => EditorWindows.Toggle(EditorLayout.SceneComponents);
    [MenuItem("Window/Inspector", 2)] static void Inspector() => EditorWindows.Toggle(EditorLayout.Inspector);
    [MenuItem("Window/Assets", 3)] static void Assets() => EditorWindows.Toggle(EditorLayout.Assets);
    [MenuItem("Window/Console", 4)] static void Console() => EditorWindows.Toggle(EditorLayout.Console);

    // ── Window menu — the standalone tool windows (Order 20+ so they sort after the panels) ─────────────
    [MenuItem("Window/Statistics", 20)] static void Statistics() => EditorWindows.Toggle(WindowKeys.Statistics);
    [MenuItem("Window/Profiler", 21)] static void Profiler() => EditorWindows.Toggle(WindowKeys.Profiler);
    [MenuItem("Window/Build", 22)] static void Build() => EditorWindows.Toggle(WindowKeys.Build);
    [MenuItem("Window/Tags & Layers", 23)] static void TagsLayers() => EditorWindows.Toggle(WindowKeys.TagsLayers);
    [MenuItem("Window/Settings", 24)] static void Settings() => EditorWindows.Toggle(WindowKeys.Settings);

    // ── Assets menu — the Unity-package importer self-registers here (was hand-listed in the Assets menu) ─
    [MenuItem("Assets/Import Unity Package...", 10)] static void ImportUnityPackage() =>
        EditorWindows.Open(WindowKeys.UnityImport);
}
