using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class ModelImporter : IAssetImporter {
    static readonly string[] Extensions = [".fbx", ".obj"];

    public string Name => "ModelImporter";
    public int Version => 1;
    public string ArtifactExtension => ".bmesh";

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        ["flipUVs"] = true,
        ["meshIndex"] = 0,
    };

    public void Import(AssetImportContext context) {
        var flipUVs = context.Settings?["flipUVs"]?.GetValue<bool>() ?? true;
        var meshIndex = context.Settings?["meshIndex"]?.GetValue<int>() ?? 0;

        MeshData data = AssimpMeshDecoder.Decode(context.SourceAbsolutePath, flipUVs, meshIndex);
        MeshArtifact.Write(context.ArtifactAbsolutePath, in data);
    }
}
