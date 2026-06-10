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

    // True for importers that have no Library artifact but still need Import() run on change
    // (e.g. the Falcor importer writes a sibling .scene). Default false = inert native asset.
    bool RunsWithoutArtifact => false;

    JsonObject CreateDefaultSettings(string assetPath);

    void Import(AssetImportContext context);
}
