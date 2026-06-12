using NVorbis;

namespace BallisticEngine.AssetPipeline;

// Decodes Ogg Vorbis (.ogg) to canonical interleaved 16-bit PCM via NVorbis (pure-C#, no native dep —
// the only managed audio codec the engine pulls in; AssetPipeline is the layer allowed external libs).
// Music and longer SFX usually ship as OGG (compressed); the engine fully decodes into RAM at import,
// same as WAV — streaming long tracks is a later optimization. Never throws; logs + returns empty.
public static class OggDecoder {
    public static AudioData Decode(string path) {
        try {
            using var reader = new VorbisReader(path);
            int channels = reader.Channels;
            int sampleRate = reader.SampleRate;
            long total = reader.TotalSamples;   // per channel

            // Read all interleaved float samples (channels * total), in chunks.
            int interleavedCount = checked((int)(total * channels));
            var floats = new float[interleavedCount];
            int read = 0;
            const int chunk = 1 << 16;
            while (read < interleavedCount) {
                int want = Math.Min(chunk, interleavedCount - read);
                int got = reader.ReadSamples(floats, read, want);
                if (got <= 0)
                    break;   // end of stream (TotalSamples can over-estimate slightly)
                read += got;
            }

            // float [-1,1] -> 16-bit signed PCM.
            var samples = new short[read];
            for (var i = 0; i < read; i++) {
                float s = Math.Clamp(floats[i], -1f, 1f);
                samples[i] = (short)(s * short.MaxValue);
            }

            return new AudioData(samples, channels, sampleRate);
        }
        catch (Exception e) {
            Debugging.LogError($"OGG decode failed for '{Path.GetFileName(path)}': {e.Message}");
            return default;
        }
    }
}
