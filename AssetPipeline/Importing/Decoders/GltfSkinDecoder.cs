using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

// A native glTF 2.0 skin reader (.glb + .gltf), used INSTEAD of Assimp for skinned models — the
// AssimpNet 4.1.0 native build silently drops glTF2 skin data (every rigged glTF reads back with
// hasBones=false), so skeletal import can't go through it. Assimp still handles static meshes, FBX,
// and material decode; this decoder owns only the skinned-glTF geometry + skeleton + animations.
//
// glTF conventions handled here:
//  - GLB = 12-byte header + JSON chunk + BIN chunk; .gltf = JSON with a sibling .bin (or data: URI).
//  - Matrices are COLUMN-major; OpenTK composes row-vector, so each MAT4 is transposed on read
//    (same role as AssimpMeshDecoder.ToOpenTK). Node TRS compose to Scale*Rotation*Translation,
//    matching Transform.LocalMatrix.
//  - JOINTS_0 is VEC4 of u8/u16 indices into skin.joints; WEIGHTS_0 is VEC4 float (or normalized int).
//  - inverseBindMatrices is one MAT4 per joint (mesh-space -> joint-space at bind).
public static class GltfSkinDecoder {
    const uint GlbMagic = 0x46546C67;   // "glTF"
    const uint ChunkJson = 0x4E4F534A;  // "JSON"
    const uint ChunkBin = 0x004E4942;   // "BIN\0"

    static readonly string[] Extensions = [".gltf", ".glb"];
    public static bool SupportsExtension(string ext) => Extensions.Contains(ext);

    // True if this is a glTF file that actually carries a skin (has a "skins" array). Cheap: parses
    // only the JSON, not the binary. Lets the importer route skinned glTF here and everything else to
    // Assimp.
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

    // ---- File loading ------------------------------------------------------

    static (JsonElement root, byte[] bin) LoadJson(string path) {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == GlbMagic)
            return LoadGlb(bytes);

        // Plain .gltf: JSON document, binary in a sibling file / data URI (resolved lazily by GltfDoc).
        var docJson = JsonDocument.Parse(bytes);
        return (docJson.RootElement.Clone(), null);
    }

    static (JsonElement root, byte[] bin) LoadGlb(byte[] bytes) {
        int offset = 12;   // magic(4) + version(4) + length(4)
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
