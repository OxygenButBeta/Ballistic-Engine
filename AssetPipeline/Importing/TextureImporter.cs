using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class TextureImporter : IAssetImporter {
    static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];

    public string Name => "TextureImporter";
    public int Version => 1;
    public string ArtifactExtension => ".btex";

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        ["textureType"] = InferTextureType(assetPath).ToString(),
    };

    // The texture type is consumed at load time (sampler slot + internal format), not baked into the artifact.
    public static TextureType TypeFromSettings(JsonObject settings) {
        var raw = settings?["textureType"]?.GetValue<string>();
        return Enum.TryParse(raw, ignoreCase: true, out TextureType type) ? type : TextureType.Diffuse;
    }

    static TextureType InferTextureType(string assetPath) {
        var stem = Path.GetFileNameWithoutExtension(assetPath).ToUpperInvariant();

        if (stem.EndsWith("_NOR") || stem.EndsWith("_NORMAL")) return TextureType.Normal;
        if (stem.EndsWith("_METAL") || stem.EndsWith("_METALLIC")) return TextureType.Metallic;
        if (stem.EndsWith("_ROUGH") || stem.EndsWith("_ROUGHNESS")) return TextureType.Roughness;
        if (stem.EndsWith("_AO")) return TextureType.AO;
        return TextureType.Diffuse;
    }

    public void Import(AssetImportContext context) {
        TextureData data = StbTextureDecoder.Decode(context.SourceAbsolutePath);
        TextureArtifact.Write(context.ArtifactAbsolutePath, in data);
    }
}
