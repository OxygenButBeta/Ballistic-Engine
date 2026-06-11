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
}
