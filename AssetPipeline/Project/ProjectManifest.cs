namespace BallisticEngine.AssetPipeline;

public sealed class ProjectManifest {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";

    // Asset reference ("Assets/..." path or "guid:<hex>") to a .cubemap asset; null disables the skybox.
    public string DefaultSkybox { get; set; }

    // Project-relative path to the scene loaded on startup (e.g. "Assets/Scenes/Main.scene"); null = empty scene.
    // Legacy single-scene field: when ScenesInBuild is empty this still drives the startup scene, so old
    // projects keep working. New projects use ScenesInBuild and StartupScene mirrors ScenesInBuild[0].
    public string StartupScene { get; set; }

    // Unity-style ordered "Scenes In Build" list of project-relative .scene paths. The first entry is the
    // startup scene; a shipped player can load any of them by name or by build index (see SceneManager.LoadScene).
    // Empty = fall back to StartupScene.
    public List<string> ScenesInBuild { get; set; } = new();

    // Player/build settings (Unity's "Player Settings"): identity baked into the shipped exe + how its
    // window comes up. Null on old projects → PlayerSettings.OrDefault(this) fills sane defaults from Name.
    public PlayerSettings Player { get; set; }
}

// Identity + window/runtime settings for a shipped build. Persisted under project.json's "player" key,
// consumed by BuildPipeline (exe metadata / icon) and the runtime window (title / mode / resolution).
public sealed class PlayerSettings {
    // ---- identity (baked into the published exe's file metadata) ----
    public string ProductName { get; set; }   // window title + <ProductName>.exe; defaults to manifest Name.
    public string CompanyName { get; set; } = "";
    public string Version { get; set; } = "1.0.0";

    // Project-relative path to a .ico embedded into the exe (taskbar/file icon). Null = engine default.
    public string IconPath { get; set; }

    // ---- window ----
    public WindowMode WindowMode { get; set; } = WindowMode.Fullscreen;
    public int Width { get; set; } = 1920;     // windowed/borderless fallback size (fullscreen uses the monitor).
    public int Height { get; set; } = 1080;

    // ---- build toolchain (power-user knobs; sensible defaults) ----
    public string Configuration { get; set; } = "Release";        // Release | Debug
    public string RuntimeIdentifier { get; set; } = "win-x64";    // win-x64 | win-arm64
    public bool SelfContained { get; set; } = true;               // bundle .NET so the target needs no runtime.

    // Returns the project's player settings, synthesising defaults (ProductName from the manifest Name)
    // for projects saved before this block existed. Never mutates the manifest.
    public static PlayerSettings OrDefault(ProjectManifest manifest) {
        var p = manifest.Player ?? new PlayerSettings();
        if (string.IsNullOrWhiteSpace(p.ProductName))
            p.ProductName = string.IsNullOrWhiteSpace(manifest.Name) ? "Game" : manifest.Name;
        return p;
    }
}

public enum WindowMode {
    Fullscreen = 0,   // borderless fullscreen at the monitor's native resolution (instant alt-tab).
    Windowed = 1,     // a resizable bordered window at Width x Height.
    Borderless = 2,   // a borderless window at Width x Height (no title bar; not monitor-sized).
}
