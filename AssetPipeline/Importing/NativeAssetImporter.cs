using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Engine-native formats (.glsl source, .mat, .shader, .cubemap, .volume, .prefab, .asset, plus the UI
// .uxml/.uss text and .ttf fonts) need no conversion: they are read straight from Assets\ at load time
// (.uxml/.uss as text via the UIDocument resolver, .ttf baked to an SDF atlas by FontLoader). The
// importer only assigns GUIDs.
public sealed class NativeAssetImporter : IAssetImporter {
    static readonly string[] Extensions =
        [".glsl", ".mat", ".shader", ".cubemap", ".volume", ".prefab", ".asset", ".uxml", ".uss", ".ttf"];

    public string Name => "NativeAssetImporter";
    public int Version => 1;
    public string ArtifactExtension => null;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        // Nothing to do.
    }
}
