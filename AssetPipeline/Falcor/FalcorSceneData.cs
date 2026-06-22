using System.Globalization;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline;

public sealed class FalcorSceneData {
    public FalcorCamera Camera { get; set; }
    public List<FalcorLight> Lights { get; } = new();
    public List<string> ModelPaths { get; } = new();
    public string EnvMapPath { get; set; }
}
