using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Imports Falcor .pyscene files. The actual conversion (parse -> Ballistic .scene) lives in the
// Engine layer (it builds engine scene documents), so it's injected via Converter — set once at
// startup by EngineBootstrap. On import this writes a sibling "<name>.scene" next to the .pyscene,
// which the next refresh picks up as a normal, openable scene asset.
public sealed class FalcorSceneImporter : IAssetImporter {
    // (pysceneAbsolutePath, outputSceneAbsolutePath) -> writes the .scene file.
    public static Action<string, string> Converter { get; set; }

    public string Name => "FalcorSceneImporter";
    public int Version => 1;
    public string ArtifactExtension => null; // produces a project asset (.scene), not a Library artifact
    public bool RunsWithoutArtifact => true;

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
