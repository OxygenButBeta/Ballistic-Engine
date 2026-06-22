using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class AnimationImporter : IAssetImporter {
    static readonly string[] Extensions = [".banim"];

    public string Name => "AnimationImporter";
    public int Version => 1;
    public string ArtifactExtension => null;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
    }
}
