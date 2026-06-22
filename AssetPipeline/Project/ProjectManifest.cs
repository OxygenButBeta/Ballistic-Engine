namespace BallisticEngine.AssetPipeline;

public sealed class ProjectManifest {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";

    public string DefaultSkybox { get; set; }

    public string StartupScene { get; set; }

    public List<string> ScenesInBuild { get; set; } = new();

    public PlayerSettings Player { get; set; }
}
