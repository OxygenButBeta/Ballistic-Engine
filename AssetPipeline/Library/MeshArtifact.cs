using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary mesh, Library\Artifacts\<guid>.bmesh:
//   u32 magic 'BMSH' | u32 version | i32 vertexCount | i32 indexCount | i32 submeshCount
//   positions[v] normals[v] tangents[v] (Vector3) | uvs[v] (Vector2) | indices[i] (u32)
//   submeshes: { i32 indexStart | i32 indexCount | string name | string materialRef } x submeshCount
//   (strings are BinaryWriter length-prefixed; "" means none)
// Version 1 had a reserved u32 instead of submeshCount and no submesh table; it reads back
// as a single submesh spanning the whole index buffer.
public static class MeshArtifact {
    const uint Magic = 0x48534D42; // "BMSH"
    const uint FormatVersion = 2;

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
        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Tangents));
        writer.Write(MemoryMarshal.AsBytes<Vector2>(data.UVs));
        writer.Write(MemoryMarshal.AsBytes<uint>(data.Indices));

        foreach (SubMeshData subMesh in data.SubMeshes) {
            writer.Write(subMesh.IndexStart);
            writer.Write(subMesh.IndexCount);
            writer.Write(subMesh.Name ?? "");
            writer.Write(subMesh.MaterialRef ?? "");
        }
    }

    public static MeshData Read(string path) {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{path}' is not a mesh artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version is not (1 or FormatVersion))
            throw new InvalidDataException($"Mesh artifact '{path}' has unsupported version {version}.");

        var vertexCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        var subMeshCount = 0;
        if (version >= 2)
            subMeshCount = reader.ReadInt32();
        else
            reader.ReadUInt32(); // v1 reserved field

        Vector3[] vertices = ReadArray<Vector3>(reader, vertexCount);
        Vector3[] normals = ReadArray<Vector3>(reader, vertexCount);
        Vector3[] tangents = ReadArray<Vector3>(reader, vertexCount);
        Vector2[] uvs = ReadArray<Vector2>(reader, vertexCount);
        uint[] indices = ReadArray<uint>(reader, indexCount);

        var subMeshes = new SubMeshData[subMeshCount];
        for (var i = 0; i < subMeshCount; i++) {
            var indexStart = reader.ReadInt32();
            var count = reader.ReadInt32();
            var name = reader.ReadString();
            var materialRef = reader.ReadString();
            subMeshes[i] = new SubMeshData(
                name.Length > 0 ? name : null,
                indexStart, count,
                materialRef.Length > 0 ? materialRef : null);
        }

        // subMeshCount == 0 (v1 artifacts): MeshData substitutes a single full-range submesh.
        return new MeshData(vertices, indices, uvs, normals, tangents, subMeshes);
    }

    static T[] ReadArray<T>(BinaryReader reader, int count) where T : unmanaged {
        var result = new T[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes<T>(result));
        return result;
    }
}
