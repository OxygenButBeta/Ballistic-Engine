using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class NativeAssetImporter : IAssetImporter {
    static readonly string[] Extensions =
        [".glsl", ".mat", ".shader", ".cubemap", ".volume", ".prefab", ".asset", ".uxml", ".uss", ".ttf"];

    public string Name => "NativeAssetImporter";
    public int Version => 1;
    public string ArtifactExtension => null;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
    }
}
