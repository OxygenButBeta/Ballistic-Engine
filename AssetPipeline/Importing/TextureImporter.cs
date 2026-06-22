using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class TextureImporter : IAssetImporter {
    static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr", ".dds"];

    public string Name => "TextureImporter";

    public int Version => 3;
    public string ArtifactExtension => ".btex";

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public static bool SupportsExtension(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        ["textureType"] = InferTextureType(assetPath).ToString(),
    };

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

    public static TextureType TypeFromSettings(JsonObject settings) {
        var raw = settings?["textureType"]?.GetValue<string>();
        return Enum.TryParse(raw, ignoreCase: true, out TextureType type) ? type : TextureType.Diffuse;
    }

    static TextureType InferTextureType(string assetPath) {
        var stem = Path.GetFileNameWithoutExtension(assetPath).ToUpperInvariant();

        var us = stem.LastIndexOf('_');
        var tag = us >= 0 ? stem[(us + 1)..] : stem;

        switch (tag) {
            case "N": case "NOR": case "NRM": case "NORM": case "NORMAL": case "NORMALS":
                return TextureType.Normal;
            case "M": case "MTL": case "METAL": case "METALLIC": case "METALNESS":
            case "SPEC": case "SPECULAR":
            case "MASK": case "ORM": case "ORD": case "DR": case "DRO": case "ORDP": case "MR": case "RMA":
                return TextureType.Metallic;
            case "R": case "RGH": case "ROUGH": case "ROUGHNESS": case "GLOSS": case "GLOSSINESS":
                return TextureType.Roughness;
            case "AO": case "O": case "OCC": case "OCCLUSION":
                return TextureType.AO;
            case "E": case "EMIS": case "EMISSIVE": case "EMISSION":
                return TextureType.Emissive;
            default:
                return TextureType.Diffuse;
        }
    }

    public void Import(AssetImportContext context) {
        TextureData data = StbTextureDecoder.Decode(context.SourceAbsolutePath);

        TextureType type = TypeFromSettings(context.Settings);
        TextureData artifact = BCnEncoder.TryPickFormat(in data, type, out TextureFormat format)
            ? CompressWithMips(in data, format)
            : data;

        TextureArtifact.Write(context.ArtifactAbsolutePath, in artifact);
    }

    static TextureData CompressWithMips(in TextureData rgba8, TextureFormat format) {
        int levels = BCnEncoder.MipLevelCount(rgba8.Width, rgba8.Height);
        long total = TextureMipLayout.ChainBytes(rgba8.Width, rgba8.Height, levels, format);
        var chain = new byte[total];

        byte[] level = rgba8.Pixels;
        int w = rgba8.Width, h = rgba8.Height;
        long offset = 0;
        for (int l = 0; l < levels; l++) {
            byte[] encoded = BCnEncoder.EncodeLevel(PadToBlock(level, w, h, out int pw, out int ph), pw, ph, format);
            encoded.CopyTo(chain.AsSpan((int)offset));
            offset += encoded.Length;

            if (l + 1 < levels)
                level = BCnEncoder.DownsampleRgba8(level, w, h, out w, out h);
        }

        return new TextureData(rgba8.Width, rgba8.Height, format, chain, levels);
    }

    static byte[] PadToBlock(byte[] rgba, int w, int h, out int pw, out int ph) {
        pw = (w + 3) & ~3;
        ph = (h + 3) & ~3;
        if (pw == w && ph == h)
            return rgba;

        var padded = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++) {
            int sy = Math.Min(y, h - 1);
            for (int x = 0; x < pw; x++) {
                int sx = Math.Min(x, w - 1);
                rgba.AsSpan((sy * w + sx) * 4, 4).CopyTo(padded.AsSpan((y * pw + x) * 4, 4));
            }
        }
        return padded;
    }
}
