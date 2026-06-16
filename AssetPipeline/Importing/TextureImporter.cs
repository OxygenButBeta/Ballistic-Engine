using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class TextureImporter : IAssetImporter {
    static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr", ".dds"];

    public string Name => "TextureImporter";
    // v2: artifacts now store GPU block-compressed mip chains (BC1/BC3/BC5) where applicable, instead
    // of raw RGBA8 — bumping forces a one-time reimport of every texture so heavy scenes stop OOMing.
    // v3: broader filename-suffix type inference (_n/_m/_dr/etc.) so scan-pack normal/packed maps
    //     stop importing as sRGB Diffuse. Forces a reimport so existing textures re-evaluate type.
    public int Version => 3;
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

        // Match by suffix after the last underscore so short tags ("_N", "_M", "_DR") are recognized
        // alongside long ones ("_NORMAL"). Critical: scan packs use terse suffixes (_n/_bc/_m/_dr) —
        // mis-typing a normal/packed map as Diffuse decodes it as sRGB color (garbled surfaces).
        var us = stem.LastIndexOf('_');
        var tag = us >= 0 ? stem[(us + 1)..] : stem;

        switch (tag) {
            case "N": case "NOR": case "NRM": case "NORM": case "NORMAL": case "NORMALS":
                return TextureType.Normal;
            case "M": case "MTL": case "METAL": case "METALLIC": case "METALNESS":
            case "SPEC": case "SPECULAR":
            // Packed mask/ORM/ORD/DR maps live in the Metallic slot (read as packed, linear).
            case "MASK": case "ORM": case "ORD": case "DR": case "DRO": case "ORDP": case "MR": case "RMA":
                return TextureType.Metallic;
            case "R": case "RGH": case "ROUGH": case "ROUGHNESS": case "GLOSS": case "GLOSSINESS":
                return TextureType.Roughness;
            case "AO": case "O": case "OCC": case "OCCLUSION":
                return TextureType.AO;
            case "E": case "EMIS": case "EMISSIVE": case "EMISSION":
                return TextureType.Emissive;
            default:
                return TextureType.Diffuse; // _BC/_D/_ALBEDO/_BASECOLOR/_COL and anything unknown
        }
    }

    public void Import(AssetImportContext context) {
        TextureData data = StbTextureDecoder.Decode(context.SourceAbsolutePath);

        TextureType type = TypeFromSettings(context.Settings);
        TextureData artifact = BCnEncoder.TryPickFormat(in data, type, out TextureFormat format)
            ? CompressWithMips(in data, format)
            : data; // HDR float or non-4-aligned: store raw RGBA8/RGBA32F, GPU mips it on upload

        TextureArtifact.Write(context.ArtifactAbsolutePath, in artifact);
    }

    // Builds the full mip chain on the CPU (box filter) and block-compresses every level, then
    // concatenates them largest-first into one payload. Mips are generated BEFORE compression because
    // GenerateMipmap can't run on a compressed top level — and a proper chain kills the shimmer that
    // a base-level-only compressed texture would show at distance.
    static TextureData CompressWithMips(in TextureData rgba8, TextureFormat format) {
        int levels = BCnEncoder.MipLevelCount(rgba8.Width, rgba8.Height);
        long total = TextureMipLayout.ChainBytes(rgba8.Width, rgba8.Height, levels, format);
        var chain = new byte[total];

        byte[] level = rgba8.Pixels;
        int w = rgba8.Width, h = rgba8.Height;
        long offset = 0;
        for (int l = 0; l < levels; l++) {
            // BC needs whole 4x4 blocks; once a mip drops below 4 we pad it up to a 4x4 minimum so the
            // block count matches TextureMipLayout (which floors dimensions but keeps a 1-block minimum).
            byte[] encoded = BCnEncoder.EncodeLevel(PadToBlock(level, w, h, out int pw, out int ph), pw, ph, format);
            encoded.CopyTo(chain.AsSpan((int)offset));
            offset += encoded.Length;

            if (l + 1 < levels)
                level = BCnEncoder.DownsampleRgba8(level, w, h, out w, out h);
        }

        return new TextureData(rgba8.Width, rgba8.Height, format, chain, levels);
    }

    // Pads an RGBA8 level up to a multiple of 4 in each dimension (edge-clamp replicate) so it can be
    // block-encoded. Most levels are already aligned; only the small tail mips of non-power-of-4 sizes
    // need it. Returns the original buffer untouched when already aligned.
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
