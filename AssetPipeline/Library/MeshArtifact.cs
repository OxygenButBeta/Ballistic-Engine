using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary mesh, Library\Artifacts\<guid>.bmesh:
//   u32 magic 'BMSH' | u32 version | i32 vertexCount | i32 indexCount | i32 submeshCount
//   positions[v] normals[v] (Vector3) | tangents[v] (Vector4, w = handedness) | uvs[v] (Vector2)
//   indices[i] (u32)
//   submeshes: { i32 indexStart | i32 indexCount | string name | string materialRef
//                | nodeTransform (Matrix4, v4+) | i32 nodeIndex (v5+) } x submeshCount
//   nodes (v5+): i32 nodeCount | { string name | i32 parentIndex | localTransform (Matrix4) }
//   skin (v6+): u8 hasSkin; if 1:
//       boneIndices[v] (Vector4i) | boneWeights[v] (Vector4)
//       i32 boneCount | { string name | i32 parentIndex | inverseBind (Matrix4) | bindLocal (Matrix4) } x boneCount
//   (strings are BinaryWriter length-prefixed; "" means none)
// Version 1 had a reserved u32 instead of submeshCount and no submesh table; it reads back
// as a single submesh spanning the whole index buffer. Versions 1-2 stored vec3 tangents;
// they read back with handedness +1. Versions 1-3 had no per-submesh node transform; they
// read back with identity (vertices are model-space baked, so rendering is unaffected).
// Version 4 had no node hierarchy table; it reads back empty (nodeIndex -1).
// Version 5 had no skin block; it reads back un-skinned (hasSkin implicitly 0).
public static class MeshArtifact {
    const uint Magic = 0x48534D42; // "BMSH"
    const uint FormatVersion = 6;

    public static void Write(string path, in MeshData data) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(data.Vertices.Length);
        writer.Write(data.Indices.Length);
        writer.Write(data.SubMeshes.Length);

        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Vertices));
        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Normals));
        writer.Write(MemoryMarshal.AsBytes<Vector4>(data.Tangents));
        writer.Write(MemoryMarshal.AsBytes<Vector2>(data.UVs));
        writer.Write(MemoryMarshal.AsBytes<uint>(data.Indices));

        foreach (SubMeshData subMesh in data.SubMeshes) {
            writer.Write(subMesh.IndexStart);
            writer.Write(subMesh.IndexCount);
            writer.Write(subMesh.Name ?? "");
            writer.Write(subMesh.MaterialRef ?? "");
            WriteMatrix(writer, subMesh.NodeTransform);
            writer.Write(subMesh.NodeIndex);
        }

        MeshNodeData[] nodes = data.Nodes ?? [];
        writer.Write(nodes.Length);
        foreach (MeshNodeData node in nodes) {
            writer.Write(node.Name ?? "");
            writer.Write(node.ParentIndex);
            WriteMatrix(writer, node.LocalTransform);
        }

        // Skin block (v6). A static mesh writes a single 0 byte.
        if (data.IsSkinned) {
            writer.Write((byte)1);
            writer.Write(MemoryMarshal.AsBytes<Vector4i>(data.BoneIndices));
            writer.Write(MemoryMarshal.AsBytes<Vector4>(data.BoneWeights));

            SkeletonData skeleton = data.Skeleton;
            writer.Write(skeleton.BoneCount);
            for (var i = 0; i < skeleton.BoneCount; i++) {
                writer.Write(skeleton.BoneNames[i] ?? "");
                writer.Write(skeleton.ParentIndices[i]);
                WriteMatrix(writer, skeleton.InverseBindPose[i]);
                WriteMatrix(writer, skeleton.BindPoseLocal[i]);
            }
        }
        else {
            writer.Write((byte)0);
        }
    }

    static void WriteMatrix(BinaryWriter writer, Matrix4 matrix) =>
        writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)));

    static Matrix4 ReadMatrix(BinaryReader reader) {
        Matrix4 matrix = default;
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref matrix, 1)));
        return matrix;
    }

    public static MeshData Read(string path) {
        using FileStream stream = File.OpenRead(path);
        return Read(stream, path);
    }

    // Decodes from an already-open stream (e.g. bytes from a mounted content pack). `sourceName` is
    // for error messages only.
    public static MeshData Read(Stream stream, string sourceName = "<stream>") {
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{sourceName}' is not a mesh artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version is < 1 or > FormatVersion)
            throw new InvalidDataException($"Mesh artifact '{sourceName}' has unsupported version {version}.");

        var vertexCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        var subMeshCount = 0;
        if (version >= 2)
            subMeshCount = reader.ReadInt32();
        else
            reader.ReadUInt32(); // v1 reserved field

        Vector3[] vertices = ReadArray<Vector3>(reader, vertexCount);
        Vector3[] normals = ReadArray<Vector3>(reader, vertexCount);
        Vector4[] tangents;
        if (version >= 3) {
            tangents = ReadArray<Vector4>(reader, vertexCount);
        }
        else {
            // Pre-handedness artifacts: widen vec3 tangents with w = +1 until reimport.
            Vector3[] legacy = ReadArray<Vector3>(reader, vertexCount);
            tangents = new Vector4[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                tangents[i] = new Vector4(legacy[i], 1f);
        }
        Vector2[] uvs = ReadArray<Vector2>(reader, vertexCount);
        uint[] indices = ReadArray<uint>(reader, indexCount);

        var subMeshes = new SubMeshData[subMeshCount];
        for (var i = 0; i < subMeshCount; i++) {
            var indexStart = reader.ReadInt32();
            var count = reader.ReadInt32();
            var name = reader.ReadString();
            var materialRef = reader.ReadString();
            Matrix4 nodeTransform = version >= 4 ? ReadMatrix(reader) : Matrix4.Identity;
            var nodeIndex = version >= 5 ? reader.ReadInt32() : -1;
            subMeshes[i] = new SubMeshData(
                name.Length > 0 ? name : null,
                indexStart, count,
                materialRef.Length > 0 ? materialRef : null,
                nodeTransform, nodeIndex);
        }

        MeshNodeData[] nodes = [];
        if (version >= 5) {
            nodes = new MeshNodeData[reader.ReadInt32()];
            for (var i = 0; i < nodes.Length; i++) {
                var name = reader.ReadString();
                var parentIndex = reader.ReadInt32();
                nodes[i] = new MeshNodeData(name.Length > 0 ? name : null, parentIndex, ReadMatrix(reader));
            }
        }

        // Skin block (v6+). Older artifacts have no byte here and read back un-skinned.
        if (version >= 6 && reader.ReadByte() == 1) {
            Vector4i[] boneIndices = ReadArray<Vector4i>(reader, vertexCount);
            Vector4[] boneWeights = ReadArray<Vector4>(reader, vertexCount);

            var boneCount = reader.ReadInt32();
            var names = new string[boneCount];
            var parents = new int[boneCount];
            var inverseBind = new Matrix4[boneCount];
            var bindLocal = new Matrix4[boneCount];
            for (var i = 0; i < boneCount; i++) {
                var name = reader.ReadString();
                names[i] = name.Length > 0 ? name : null;
                parents[i] = reader.ReadInt32();
                inverseBind[i] = ReadMatrix(reader);
                bindLocal[i] = ReadMatrix(reader);
            }
            var skeleton = new SkeletonData(names, parents, inverseBind, bindLocal);
            return new MeshData(vertices, indices, uvs, normals, tangents, subMeshes, nodes,
                boneIndices, boneWeights, skeleton);
        }

        // subMeshCount == 0 (v1 artifacts): MeshData substitutes a single full-range submesh.
        return new MeshData(vertices, indices, uvs, normals, tangents, subMeshes, nodes);
    }

    static T[] ReadArray<T>(BinaryReader reader, int count) where T : unmanaged {
        var result = new T[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes<T>(result));
        return result;
    }
}
