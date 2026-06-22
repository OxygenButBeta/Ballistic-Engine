using System.Text.Json;
using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class TerrainImporter : IAssetImporter {
    public string Name => "TerrainImporter";

    public int Version => 1;

    public string ArtifactExtension => ".bterrain";

    public bool CanImport(string extension) => extension == ".terrain";

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        TerrainDefinition definition = ReadDefinition(context.SourceAbsolutePath);
        TerrainData data = ToData(definition);
        TerrainArtifact.Write(context.ArtifactAbsolutePath, in data);
    }

    static TerrainDefinition ReadDefinition(string sourceAbsolutePath) {
        try {
            var text = File.ReadAllText(sourceAbsolutePath);
            return JsonSerializer.Deserialize<TerrainDefinition>(text, PipelineJson.Options)
                   ?? new TerrainDefinition();
        }
        catch (Exception exception) {
            Debugging.LogError($"'{sourceAbsolutePath}': terrain source unreadable ({exception.Message}); importing flat.");
            return new TerrainDefinition();
        }
    }

    public static TerrainData ToData(TerrainDefinition definition) {
        int resolution = Math.Clamp(definition.Resolution, 2, 4096);
        var size = new Vector2(MathF.Max(definition.SizeX, 0.01f), MathF.Max(definition.SizeZ, 0.01f));
        float heightScale = definition.HeightScale;
        int count = resolution * resolution;

        if (!TerrainHeightCodec.TryDecode(definition.Heights, count, out float[] heights))
            heights = new float[count];

        return new TerrainData(resolution, size, heightScale, heights);
    }
}
