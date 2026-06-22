using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class FalcorSceneImporter : IAssetImporter {
    public static Action<string, string> Converter { get; set; }

    public string Name => "FalcorSceneImporter";
    public int Version => 1;
    public string ArtifactExtension => null;
    public bool RunsWithoutArtifact => true;
    public bool GeneratesSourceAssets => true;

    public bool CanImport(string extension) => extension == ".pyscene";

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        if (Converter is null) {
            Debugging.LogWarning($"Falcor importer not wired; skipping '{context.AssetPath}'.");
            return;
        }

        var outputPath = Path.ChangeExtension(context.SourceAbsolutePath, ".scene");
        Converter(context.SourceAbsolutePath, outputPath);
        Debugging.Log($"Falcor: imported '{context.AssetPath}' -> '{Path.GetFileName(outputPath)}'.");
    }
}
