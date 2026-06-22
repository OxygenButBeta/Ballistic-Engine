using System.IO.Compression;

namespace BallisticEngine.AssetPipeline;

public static class TextureArtifact {
    const uint Magic = 0x58455442;
    const uint FormatVersion = 2;

    public static void Write(string path, in TextureData data, bool compress = true) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(data.Width);
        writer.Write(data.Height);
        writer.Write((byte)data.Format);
        writer.Write((byte)(compress ? 1 : 0));
        writer.Write(data.MipCount);
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
        return Read(stream, path);
    }

    public static TextureData Read(Stream stream, string name = "<stream>") {
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{name}' is not a texture artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version is not (1 or FormatVersion))
            throw new InvalidDataException($"Texture artifact '{name}' has unsupported version {version}.");

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var format = (TextureFormat)reader.ReadByte();
        var compressed = reader.ReadByte() == 1;
        var mipCount = version >= 2 ? reader.ReadInt32() : 1;
        var pixelByteCount = reader.ReadInt64();

        var pixels = new byte[pixelByteCount];
        if (compressed) {
            using DeflateStream deflate = new(stream, CompressionMode.Decompress);
            deflate.ReadExactly(pixels);
        }
        else {
            stream.ReadExactly(pixels);
        }

        return new TextureData(width, height, format, pixels, mipCount);
    }
}
