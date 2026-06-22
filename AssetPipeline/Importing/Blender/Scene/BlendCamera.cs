using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

public sealed class BlendCamera {
    public string Name = "Camera";
    public float[] Matrix = BlendSceneParser.Identity4();
    public float FovY = 0.69f;
    public float Near = 0.1f;
    public float Far = 1000f;
    public bool IsActive = true;
}
