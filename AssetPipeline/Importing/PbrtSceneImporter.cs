using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Imports pbrt scene files (.pbrt, v3 and v4). Like the Falcor importer, the actual conversion
// (parse -> Ballistic .scene + generated .mat files) lives in the Engine layer and is injected via
// Converter (set once at startup by EngineBootstrap). On import this writes a sibling "<name>.scene"
// next to the .pbrt plus a "<name>_Materials/" folder, which the next refresh registers as assets.
public sealed class PbrtSceneImporter : IAssetImporter {
    // (pbrtAbsolutePath, outputSceneAbsolutePath) -> writes the .scene file (and sibling materials).
    public static Action<string, string> Converter { get; set; }

    public string Name => "PbrtSceneImporter";
    public int Version => 1;
    public string ArtifactExtension => null; // produces a project asset (.scene), not a Library artifact
    public bool RunsWithoutArtifact => true;
    public bool GeneratesSourceAssets => true; // writes a sibling .scene and .mat files

    public bool CanImport(string extension) => extension == ".pbrt";

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        if (Converter is null) {
            Debugging.LogWarning($"pbrt importer not wired; skipping '{context.AssetPath}'.");
            return;
        }

        var outputPath = Path.ChangeExtension(context.SourceAbsolutePath, ".scene");
        Converter(context.SourceAbsolutePath, outputPath);
        Debugging.Log($"pbrt: imported '{context.AssetPath}' -> '{Path.GetFileName(outputPath)}'.");
    }
}
