using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

public sealed class BlendSceneData {
    public bool HasMesh { get; set; }
    public List<BlendMesh> Meshes { get; } = new();
    public List<BlendCamera> Cameras { get; } = new();
    public List<BlendLight> Lights { get; } = new();
}
