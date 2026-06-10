using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public interface IAssetImporter {
    // Stored in .meta "importer"; must stay stable.
    string Name { get; }

    // Bump to force a reimport of every asset this importer owns.
    int Version { get; }

    // Lowercase extension including the dot, e.g. ".fbx".
    bool CanImport(string extension);

    // ".bmesh"/".btex", or null when the importer produces no Library artifact
    // (engine-native text assets are read straight from Assets\).
    string ArtifactExtension { get; }

    JsonObject CreateDefaultSettings(string assetPath);

    void Import(AssetImportContext context);
}
