using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary mesh, Library\Artifacts\<guid>.bmesh:
//   u32 magic 'BMSH' | u32 version | i32 vertexCount | i32 indexCount | u32 reserved
//   positions[v] normals[v] tangents[v] (Vector3) | uvs[v] (Vector2) | indices[i] (u32)
public static class MeshArtifact {
    const uint Magic = 0x48534D42; // "BMSH"
    const uint FormatVersion = 1;

    public static void Write(string path, in MeshData data) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(data.Vertices.Length);
        writer.Write(data.Indices.Length);
        writer.Write(0u);

        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Vertices));
        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Normals));
        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Tangents));
        writer.Write(MemoryMarshal.AsBytes<Vector2>(data.UVs));
        writer.Write(MemoryMarshal.AsBytes<uint>(data.Indices));
    }

    public static MeshData Read(string path) {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{path}' is not a mesh artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Mesh artifact '{path}' has unsupported version {version}.");

        var vertexCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        reader.ReadUInt32(); // reserved

        Vector3[] vertices = ReadArray<Vector3>(reader, vertexCount);
        Vector3[] normals = ReadArray<Vector3>(reader, vertexCount);
        Vector3[] tangents = ReadArray<Vector3>(reader, vertexCount);
        Vector2[] uvs = ReadArray<Vector2>(reader, vertexCount);
        uint[] indices = ReadArray<uint>(reader, indexCount);

        return new MeshData(vertices, indices, uvs, normals, tangents);
    }

    static T[] ReadArray<T>(BinaryReader reader, int count) where T : unmanaged {
        var result = new T[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes<T>(result));
        return result;
    }
}
