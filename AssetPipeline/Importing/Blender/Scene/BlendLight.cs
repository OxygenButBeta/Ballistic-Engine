using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

public sealed class BlendLight {
    public string Name = "Light";
    public float[] Matrix = BlendSceneParser.Identity4();
    public string LightType = "POINT";
    public float[] Color = [1f, 1f, 1f];
    public float Energy = 1000f;
    public float Range;
    public float SpotSize = 1.2f;
    public float SpotBlend = 0.15f;
}
