namespace BallisticEngine.AssetPipeline;

public sealed class ProjectManifest {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";

    // Asset reference ("Assets/..." path or "guid:<hex>") to a .cubemap asset; null disables the skybox.
    public string DefaultSkybox { get; set; }
}
