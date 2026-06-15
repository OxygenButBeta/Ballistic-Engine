using System.Text.Json;
using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// .terrain (JSON source) -> .bterrain (binary artifact). The source carries the authoritative
// height field (a base64 Deflate blob, see TerrainDefinition/TerrainHeightCodec); Import expands it
// into the fast-load binary artifact TerrainLoader reads. A source with no/blank heights imports as
// a flat field, so a freshly created .terrain is valid immediately.
public sealed class TerrainImporter : IAssetImporter {
    public string Name => "TerrainImporter";

    // v1: bump to force a reimport of every .terrain when the artifact format changes.
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

    // Converts the on-disk definition into a CPU height field, clamping invalid sizes/resolutions
    // and falling back to a flat field when the height blob is missing or malformed.
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
