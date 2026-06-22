namespace BallisticEngine.Editor;

internal static class EditorMenus {
    public static readonly IReadOnlyDictionary<string, string> PathToWindowKey = new Dictionary<string, string> {
        ["Window/Entities"] = EditorLayout.Entities,
        ["Window/Scene Components"] = EditorLayout.SceneComponents,
        ["Window/Details"] = EditorLayout.Inspector,
        ["Window/Assets"] = EditorLayout.Assets,
        ["Window/Console"] = EditorLayout.Console,
        ["Window/Statistics"] = WindowKeys.Statistics,
        ["Window/Profiler"] = WindowKeys.Profiler,
        ["Window/Build"] = WindowKeys.Build,
        ["Window/Tags & Layers"] = WindowKeys.TagsLayers,
        ["Window/Layer Collision Matrix"] = WindowKeys.LayerCollision,
        ["Window/Settings"] = WindowKeys.Settings,
    };

    public static class WindowKeys {
        public const string Statistics = "##win.statistics";
        public const string Profiler = "##win.profiler";
        public const string Build = "##win.build";
        public const string TagsLayers = "##win.tagslayers";
        public const string LayerCollision = "##win.layercollision";
        public const string Settings = "##win.settings";
        public const string UnityImport = "##win.unityimport";
    }

    [MenuItem("Window/Entities", 0)] static void Entities() => EditorWindows.Toggle(EditorLayout.Entities);
    [MenuItem("Window/Scene Components", 1)] static void SceneComponents() => EditorWindows.Toggle(EditorLayout.SceneComponents);
    [MenuItem("Window/Details", 2)] static void Inspector() => EditorWindows.Toggle(EditorLayout.Inspector);
    [MenuItem("Window/Assets", 3)] static void Assets() => EditorWindows.Toggle(EditorLayout.Assets);
    [MenuItem("Window/Console", 4)] static void Console() => EditorWindows.Toggle(EditorLayout.Console);

    [MenuItem("Window/Statistics", 20)] static void Statistics() => EditorWindows.Toggle(WindowKeys.Statistics);
    [MenuItem("Window/Profiler", 21)] static void Profiler() => EditorWindows.Toggle(WindowKeys.Profiler);
    [MenuItem("Window/Build", 22)] static void Build() => EditorWindows.Toggle(WindowKeys.Build);
    [MenuItem("Window/Tags & Layers", 23)] static void TagsLayers() => EditorWindows.Toggle(WindowKeys.TagsLayers);
    [MenuItem("Window/Layer Collision Matrix", 25)] static void LayerCollision() => EditorWindows.Toggle(WindowKeys.LayerCollision);

    [MenuItem("Window/Settings", 24)] static void Settings() => EditorWindows.Toggle(WindowKeys.Settings);

    [MenuItem("Assets/Import Unity Package...", 10)] static void ImportUnityPackage() =>
        EditorWindows.Open(WindowKeys.UnityImport);
}
