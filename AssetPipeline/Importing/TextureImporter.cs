using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class TextureImporter : IAssetImporter {
    static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr", ".dds"];

    public string Name => "TextureImporter";
    public int Version => 1;
    public string ArtifactExtension => ".btex";

    public bool CanImport(string extension) => Extensions.Contains(extension);

    // For other importers (models) that need to know whether a referenced file is importable.
    public static bool SupportsExtension(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        ["textureType"] = InferTextureType(assetPath).ToString(),
    };

    // Self-heal stale metas: older .meta files (created before name-based inference, or by an earlier
    // importer) left normal/spec/rough maps tagged Diffuse — they then bind through the wrong sampler
    // (sRGB color path instead of linear data), producing garbled surfaces. If the stored type is the
    // DEFAULT Diffuse but the filename clearly infers a data map, correct it. Only upgrades AWAY from
    // Diffuse, so a deliberate non-default choice is never overridden.
    public bool UpgradeSettings(string assetPath, JsonObject settings) {
        TextureType current = TypeFromSettings(settings);
        if (current != TextureType.Diffuse)
            return false;

        TextureType inferred = InferTextureType(assetPath);
        if (inferred == TextureType.Diffuse)
            return false;

        settings["textureType"] = inferred.ToString();
        return true;
    }

    // The texture type is consumed at load time (sampler slot + internal format), not baked into the artifact.
    public static TextureType TypeFromSettings(JsonObject settings) {
        var raw = settings?["textureType"]?.GetValue<string>();
        return Enum.TryParse(raw, ignoreCase: true, out TextureType type) ? type : TextureType.Diffuse;
    }

    static TextureType InferTextureType(string assetPath) {
        var stem = Path.GetFileNameWithoutExtension(assetPath).ToUpperInvariant();

        if (stem.EndsWith("_NOR") || stem.EndsWith("_NORMAL") || stem.EndsWith("_NORMALS") || stem.EndsWith("_NRM"))
            return TextureType.Normal;
        if (stem.EndsWith("_METAL") || stem.EndsWith("_METALLIC") || stem.EndsWith("_METALNESS") ||
            stem.EndsWith("_SPEC") || stem.EndsWith("_SPECULAR"))
            return TextureType.Metallic;
        if (stem.EndsWith("_ROUGH") || stem.EndsWith("_ROUGHNESS") || stem.EndsWith("_GLOSS") ||
            stem.EndsWith("_GLOSSINESS"))
            return TextureType.Roughness;
        if (stem.EndsWith("_AO") || stem.EndsWith("_OCCLUSION"))
            return TextureType.AO;
        return TextureType.Diffuse;
    }

    public void Import(AssetImportContext context) {
        TextureData data = StbTextureDecoder.Decode(context.SourceAbsolutePath);
        TextureArtifact.Write(context.ArtifactAbsolutePath, in data);
    }
}
