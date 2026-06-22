using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public interface IAssetImporter {
    string Name { get; }

    int Version { get; }

    bool CanImport(string extension);

    string ArtifactExtension { get; }

    bool RunsWithoutArtifact => false;

    bool GeneratesSourceAssets => false;

    JsonObject CreateDefaultSettings(string assetPath);

    bool UpgradeSettings(string assetPath, JsonObject settings) => false;

    void Import(AssetImportContext context);
}
