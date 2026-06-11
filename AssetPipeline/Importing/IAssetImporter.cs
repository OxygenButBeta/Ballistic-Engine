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

    // True for importers that WRITE new source assets into Assets\ during Import() (the model
    // importer generates a sibling .mat folder; the Falcor importer writes a .scene). Only these
    // require the pipeline to sweep again so the generated files get registered. Default false
    // lets a refresh that imported only "leaf" assets (textures, meshes) finish in a single pass.
    bool GeneratesSourceAssets => false;

    JsonObject CreateDefaultSettings(string assetPath);

    // Heals stale .meta settings of EXISTING assets during a refresh — e.g. a texture whose type is
    // still the default but whose filename clearly indicates Normal/Spec. Mutates `settings` in place
    // and returns true if it changed anything (the pipeline then rewrites the .meta and reimports).
    // Default: no upgrade. MUST be conservative — never override a deliberate non-default user choice.
    bool UpgradeSettings(string assetPath, JsonObject settings) => false;

    void Import(AssetImportContext context);
}
