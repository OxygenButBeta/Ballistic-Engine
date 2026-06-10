using System.IO.Compression;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary texture, Library\Artifacts\<guid>.btex:
//   u32 magic 'BTEX' | u32 version | i32 width | i32 height | u8 format | u8 compression | i64 pixelByteCount
//   payload: raw RGBA8, Deflate-compressed when compression == 1
public static class TextureArtifact {
    const uint Magic = 0x58455442; // "BTEX"
    const uint FormatVersion = 1;

    public static void Write(string path, in TextureData data, bool compress = true) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(data.Width);
        writer.Write(data.Height);
        writer.Write((byte)data.Format);
        writer.Write((byte)(compress ? 1 : 0));
        writer.Write((long)data.Pixels.Length);

        if (compress) {
            using DeflateStream deflate = new(stream, CompressionLevel.Fastest, leaveOpen: true);
            deflate.Write(data.Pixels);
        }
        else {
            writer.Write(data.Pixels);
        }
    }

    public static TextureData Read(string path) {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{path}' is not a texture artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Texture artifact '{path}' has unsupported version {version}.");

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var format = (TextureFormat)reader.ReadByte();
        var compressed = reader.ReadByte() == 1;
        var pixelByteCount = reader.ReadInt64();

        var pixels = new byte[pixelByteCount];
        if (compressed) {
            using DeflateStream deflate = new(stream, CompressionMode.Decompress);
            deflate.ReadExactly(pixels);
        }
        else {
            stream.ReadExactly(pixels);
        }

        return new TextureData(width, height, format, pixels);
    }
}
