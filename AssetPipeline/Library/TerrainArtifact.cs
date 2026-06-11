using System.IO.Compression;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary terrain height field, Library\Artifacts\<guid>.bterrain:
//   u32 magic 'BTRN' | u32 version | i32 resolution | f32 sizeX | f32 sizeZ | f32 heightScale |
//   u8 compression | i64 heightByteCount | payload: raw float[] heights, Deflate-compressed when 1
//
// Mirrors TextureArtifact's layout/compression. Heights are a row-major resolution x resolution
// grid in [0,1] (see TerrainData). Deflate keeps a 256^2 field (256 KB raw) well under the size of
// the equivalent YAML, and a 1024^2 field (4 MB raw) practical to load.
public static class TerrainArtifact {
    const uint Magic = 0x4E525442; // "BTRN"
    const uint FormatVersion = 1;

    public static void Write(string path, in TerrainData data, bool compress = true) {
        using FileStream stream = File.Create(path);
        Write(stream, in data, compress);
    }

    public static void Write(Stream stream, in TerrainData data, bool compress = true) {
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(data.Resolution);
        writer.Write(data.Size.X);
        writer.Write(data.Size.Y);
        writer.Write(data.HeightScale);
        writer.Write((byte)(compress ? 1 : 0));
        writer.Write((long)data.Heights.Length * sizeof(float));
        writer.Flush();

        var bytes = new byte[data.Heights.Length * sizeof(float)];
        Buffer.BlockCopy(data.Heights, 0, bytes, 0, bytes.Length);

        if (compress) {
            using DeflateStream deflate = new(stream, CompressionLevel.Fastest, leaveOpen: true);
            deflate.Write(bytes);
        }
        else {
            stream.Write(bytes);
        }
    }

    public static TerrainData Read(string path) {
        using FileStream stream = File.OpenRead(path);
        return Read(stream, path);
    }

    // Decodes from an already-open stream (e.g. bytes from a mounted content pack). `name` is for
    // error messages only.
    public static TerrainData Read(Stream stream, string name = "<stream>") {
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{name}' is not a terrain artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Terrain artifact '{name}' has unsupported version {version}.");

        var resolution = reader.ReadInt32();
        var sizeX = reader.ReadSingle();
        var sizeZ = reader.ReadSingle();
        var heightScale = reader.ReadSingle();
        var compressed = reader.ReadByte() == 1;
        var heightByteCount = reader.ReadInt64();

        var bytes = new byte[heightByteCount];
        if (compressed) {
            using DeflateStream deflate = new(stream, CompressionMode.Decompress);
            deflate.ReadExactly(bytes);
        }
        else {
            stream.ReadExactly(bytes);
        }

        var heights = new float[heightByteCount / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);

        return new TerrainData(resolution, new Vector2(sizeX, sizeZ), heightScale, heights);
    }
}
