using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// .banim animation clips are engine-native binary artifacts WRITTEN by the ModelImporter (one sibling
// per source animation), then read straight back from the project by AnimationClipLoader — exactly
// like .mat/.shader/.ttf native assets. This importer therefore produces no artifact and does no
// conversion; it only assigns/keeps the GUID (the ModelImporter already stamps a .meta on first write).
public sealed class AnimationImporter : IAssetImporter {
    static readonly string[] Extensions = [".banim"];

    public string Name => "AnimationImporter";
    public int Version => 1;
    public string ArtifactExtension => null;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        // Nothing to do — the .banim is already the loadable artifact.
    }
}
