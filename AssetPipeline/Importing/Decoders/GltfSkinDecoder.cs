using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

public static class GltfSkinDecoder {
    const uint GlbMagic = 0x46546C67;
    const uint ChunkJson = 0x4E4F534A;
    const uint ChunkBin = 0x004E4942;

    static readonly string[] Extensions = [".gltf", ".glb"];
    public static bool SupportsExtension(string ext) => Extensions.Contains(ext);

    public static bool HasSkin(string path) {
        try {
            (JsonElement root, _) = LoadJson(path);
            return root.TryGetProperty("skins", out JsonElement skins)
                && skins.ValueKind == JsonValueKind.Array && skins.GetArrayLength() > 0;
        }
        catch {
            return false;
        }
    }

    public static AssimpSkinDecoder.DecodedSkinnedModel Decode(string path, bool flipUVs = true) {
        (JsonElement root, byte[] bin) = LoadJson(path);
        var doc = new GltfDoc(root, bin, Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.GetFileNameWithoutExtension(path));

        SkeletonData skeleton = doc.BuildSkeleton(out var jointNodeToBone);
        MeshData mesh = doc.BuildSkinnedMesh(skeleton, jointNodeToBone, flipUVs);
        AnimationClipData[] animations = doc.BuildAnimations(jointNodeToBone, skeleton.BoneNames);

        return new AssimpSkinDecoder.DecodedSkinnedModel {
            Mesh = mesh,
            SubMeshMaterials = doc.BuildMaterials(mesh.SubMeshes.Length),
            Animations = animations,
        };
    }

    static (JsonElement root, byte[] bin) LoadJson(string path) {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == GlbMagic)
            return LoadGlb(bytes);

        var docJson = JsonDocument.Parse(bytes);
        return (docJson.RootElement.Clone(), null);
    }

    static (JsonElement root, byte[] bin) LoadGlb(byte[] bytes) {
        int offset = 12;
        JsonElement root = default;
        byte[] bin = null;
        while (offset + 8 <= bytes.Length) {
            uint chunkLength = BitConverter.ToUInt32(bytes, offset);
            uint chunkType = BitConverter.ToUInt32(bytes, offset + 4);
            int dataStart = offset + 8;
            if (dataStart + chunkLength > bytes.Length)
                break;

            if (chunkType == ChunkJson) {
                using JsonDocument doc = JsonDocument.Parse(bytes.AsSpan(dataStart, (int)chunkLength).ToArray());
                root = doc.RootElement.Clone();
            }
            else if (chunkType == ChunkBin) {
                bin = bytes.AsSpan(dataStart, (int)chunkLength).ToArray();
            }
            offset = dataStart + (int)chunkLength;
        }
        return (root, bin);
    }
}
