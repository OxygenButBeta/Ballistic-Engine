namespace BallisticEngine.AssetPipeline;

// Minimal RIFF/WAVE decoder (BCL only — no NuGet). Handles the formats game audio actually ships as:
// PCM 8/16/24/32-bit integer and IEEE float 32-bit, mono or stereo, any sample rate. Everything is
// converted to interleaved 16-bit signed PCM (AudioData's canonical form), which is what OpenAL
// uploads natively. Returns an empty AudioData (logged) on anything it can't parse — never throws,
// matching the engine's asset conventions.
public static class WavDecoder {
    const ushort FormatPcm = 1;
    const ushort FormatFloat = 3;
    const ushort FormatExtensible = 0xFFFE;

    public static AudioData Decode(string path) {
        try {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            return DecodeStream(reader, path);
        }
        catch (Exception e) {
            Debugging.LogError($"WAV decode failed for '{Path.GetFileName(path)}': {e.Message}");
            return default;
        }
    }

    // Reads a 4-byte ASCII chunk tag. NOT BinaryReader.ReadChars — that decodes through the
    // stream's UTF-8 encoding and can consume a variable number of bytes when the data that follows
    // a tag isn't valid UTF-8 (raw PCM rarely is), desyncing the whole chunk walk.
    static string ReadTag(BinaryReader reader) {
        Span<byte> tag = stackalloc byte[4];
        return reader.Read(tag) == 4 ? System.Text.Encoding.ASCII.GetString(tag) : "";
    }

    static AudioData DecodeStream(BinaryReader reader, string path) {
        // RIFF header: "RIFF" <u32 size> "WAVE"
        if (ReadTag(reader) != "RIFF") {
            Debugging.LogError($"'{Path.GetFileName(path)}' is not a RIFF file.");
            return default;
        }
        reader.ReadUInt32(); // overall chunk size (unused)
        if (ReadTag(reader) != "WAVE") {
            Debugging.LogError($"'{Path.GetFileName(path)}' is not a WAVE file.");
            return default;
        }

        ushort format = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0;
        byte[] dataBytes = null;

        // Walk chunks until we've read both "fmt " and "data".
        long length = reader.BaseStream.Length;
        while (reader.BaseStream.Position + 8 <= length) {
            string chunkId = ReadTag(reader);
            uint chunkSize = reader.ReadUInt32();
            long next = reader.BaseStream.Position + chunkSize + (chunkSize & 1); // chunks are word-aligned

            if (chunkId == "fmt ") {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32(); // byte rate
                reader.ReadUInt16(); // block align
                bitsPerSample = reader.ReadUInt16();
                // WAVE_FORMAT_EXTENSIBLE buries the real format tag in its sub-format GUID's first
                // 2 bytes; for our purposes the bit depth alone disambiguates PCM vs float.
                if (format == FormatExtensible)
                    format = bitsPerSample == 32 ? FormatFloat : FormatPcm;
            }
            else if (chunkId == "data") {
                dataBytes = reader.ReadBytes((int)chunkSize);
            }

            // Seek to the next chunk. Guard only against a chunk that would move us BACKWARDS
            // (corrupt size) — next == position is the normal "consumed exactly" case and must
            // NOT break, or we'd stop right after fmt and never reach data.
            if (next < reader.BaseStream.Position) break;
            reader.BaseStream.Position = next;
            if (dataBytes != null && format != 0) break;
        }

        if (dataBytes is null || channels == 0 || sampleRate == 0) {
            Debugging.LogError($"'{Path.GetFileName(path)}': missing fmt/data chunk.");
            return default;
        }

        short[] samples = ConvertToPcm16(dataBytes, format, bitsPerSample, path);
        if (samples is null)
            return default;

        return new AudioData(samples, channels, sampleRate);
    }

    // Converts a raw sample blob in the source format to 16-bit signed PCM.
    static short[] ConvertToPcm16(byte[] bytes, ushort format, ushort bits, string path) {
        switch (format) {
            case FormatPcm when bits == 16: {
                var outp = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, outp, 0, outp.Length * 2);
                return outp;
            }
            case FormatPcm when bits == 8: {
                // 8-bit WAV is UNSIGNED (0..255, 128 = silence) — recenter and scale to 16-bit.
                var outp = new short[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                    outp[i] = (short)((bytes[i] - 128) << 8);
                return outp;
            }
            case FormatPcm when bits == 24: {
                int count = bytes.Length / 3;
                var outp = new short[count];
                for (int i = 0; i < count; i++) {
                    // Little-endian 24-bit signed; the high two bytes ARE the top 16 bits (the
                    // low byte is dropped). (sbyte) on the MSB sign-extends correctly.
                    outp[i] = (short)(((sbyte)bytes[i * 3 + 2] << 8) | bytes[i * 3 + 1]);
                }
                return outp;
            }
            case FormatPcm when bits == 32: {
                int count = bytes.Length / 4;
                var outp = new short[count];
                for (int i = 0; i < count; i++) {
                    int sample = BitConverter.ToInt32(bytes, i * 4);
                    outp[i] = (short)(sample >> 16);
                }
                return outp;
            }
            case FormatFloat when bits == 32: {
                int count = bytes.Length / 4;
                var outp = new short[count];
                for (int i = 0; i < count; i++) {
                    float sample = BitConverter.ToSingle(bytes, i * 4);
                    sample = Math.Clamp(sample, -1f, 1f);
                    outp[i] = (short)(sample * short.MaxValue);
                }
                return outp;
            }
            default:
                Debugging.LogError(
                    $"'{Path.GetFileName(path)}': unsupported WAV format (tag {format}, {bits}-bit).");
                return null;
        }
    }
}
