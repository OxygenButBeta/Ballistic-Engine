using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

public sealed class BlendMesh {
    public string Name = "Mesh";
    public float[] Matrix = BlendSceneParser.Identity4();
}
