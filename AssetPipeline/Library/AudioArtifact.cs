using System.Runtime.InteropServices;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary audio, Library\Artifacts\<guid>.baud:
//   u32 magic 'BAUD' | u32 version | i32 channels | i32 sampleRate | i32 sampleCount | i16[sampleCount]
// Samples are interleaved 16-bit signed PCM (AudioData's canonical form). The decode step (WAV/OGG)
// already normalized to this, so loading is a straight blit — no per-load format conversion.
public static class AudioArtifact {
    const uint Magic = 0x44554142; // "BAUD"
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
        reader.ReadUInt32(); // version (only 1 exists)
        int channels = reader.ReadInt32();
        int sampleRate = reader.ReadInt32();
        int sampleCount = reader.ReadInt32();

        var samples = new short[sampleCount];
        byte[] raw = reader.ReadBytes(sampleCount * sizeof(short));
        Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);

        return new AudioData(samples, channels, sampleRate);
    }
}
