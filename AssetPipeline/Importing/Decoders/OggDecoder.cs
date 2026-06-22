using NVorbis;

namespace BallisticEngine.AssetPipeline;

public static class OggDecoder {
    public static AudioData Decode(string path) {
        try {
            using var reader = new VorbisReader(path);
            int channels = reader.Channels;
            int sampleRate = reader.SampleRate;
            long total = reader.TotalSamples;

            int interleavedCount = checked((int)(total * channels));
            var floats = new float[interleavedCount];
            int read = 0;
            const int chunk = 1 << 16;
            while (read < interleavedCount) {
                int want = Math.Min(chunk, interleavedCount - read);
                int got = reader.ReadSamples(floats, read, want);
                if (got <= 0)
                    break;
                read += got;
            }

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
