using System.Runtime.InteropServices;

namespace BallisticEngine.AssetPipeline;

public static class AudioArtifact {
    const uint Magic = 0x44554142;
    const uint FormatVersion = 1;

    public static void Write(string path, in AudioData data) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        short[] samples = data.Samples ?? System.Array.Empty<short>();
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(data.Channels);
        writer.Write(data.SampleRate);
        writer.Write(samples.Length);
        writer.Write(MemoryMarshal.AsBytes<short>(samples));
    }

    public static AudioData Read(Stream stream, string assetPath = null) {
        using BinaryReader reader = new(stream);

        uint magic = reader.ReadUInt32();
        if (magic != Magic) {
            Debugging.LogError($"'{assetPath ?? "audio"}': not a BAUD artifact (bad magic).");
            return default;
        }
        reader.ReadUInt32();
        int channels = reader.ReadInt32();
        int sampleRate = reader.ReadInt32();
        int sampleCount = reader.ReadInt32();

        var samples = new short[sampleCount];
        byte[] raw = reader.ReadBytes(sampleCount * sizeof(short));
        Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);

        return new AudioData(samples, channels, sampleRate);
    }
}
