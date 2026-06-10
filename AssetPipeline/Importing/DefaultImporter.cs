using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Fallback for unrecognized files: GUID-tracked, no artifact, not loadable.
public sealed class DefaultImporter : IAssetImporter {
    public string Name => "DefaultImporter";
    public int Version => 1;
    public string ArtifactExtension => null;

    public bool CanImport(string extension) => true;

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        // Nothing to do.
    }
}
