namespace BallisticEngine.AssetPipeline;

public sealed class ProjectManifest {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";

    public string DefaultSkybox { get; set; }

    public string StartupScene { get; set; }

    public List<string> ScenesInBuild { get; set; } = new();

    public PlayerSettings Player { get; set; }
}

public sealed class PlayerSettings {
    public string ProductName { get; set; }
    public string CompanyName { get; set; } = "";
    public string Version { get; set; } = "1.0.0";

    public string IconPath { get; set; }

    public WindowMode WindowMode { get; set; } = WindowMode.Fullscreen;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    public string Configuration { get; set; } = "Release";
    public string RuntimeIdentifier { get; set; } = "win-x64";
    public bool SelfContained { get; set; } = true;

    public static PlayerSettings OrDefault(ProjectManifest manifest) {
        var p = manifest.Player ?? new PlayerSettings();
        if (string.IsNullOrWhiteSpace(p.ProductName))
            p.ProductName = string.IsNullOrWhiteSpace(manifest.Name) ? "Game" : manifest.Name;
        return p;
    }
}

public enum WindowMode {
    Fullscreen = 0,
    Windowed = 1,
    Borderless = 2,
}
