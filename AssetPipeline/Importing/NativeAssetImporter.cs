using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Engine-native formats (.glsl source, .mat, .shader, .cubemap) need no conversion:
// they are read straight from Assets\ at load time. The importer only assigns GUIDs.
public sealed class NativeAssetImporter : IAssetImporter {
    static readonly string[] Extensions = [".glsl", ".mat", ".shader", ".cubemap"];

    public string Name => "NativeAssetImporter";
    public int Version => 1;
    public string ArtifactExtension => null;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        // Nothing to do.
    }
}
