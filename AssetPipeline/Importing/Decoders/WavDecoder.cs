namespace BallisticEngine.AssetPipeline;

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

    static string ReadTag(BinaryReader reader) {
        Span<byte> tag = stackalloc byte[4];
        return reader.Read(tag) == 4 ? System.Text.Encoding.ASCII.GetString(tag) : "";
    }

    static AudioData DecodeStream(BinaryReader reader, string path) {
        if (ReadTag(reader) != "RIFF") {
            Debugging.LogError($"'{Path.GetFileName(path)}' is not a RIFF file.");
            return default;
        }
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE") {
            Debugging.LogError($"'{Path.GetFileName(path)}' is not a WAVE file.");
            return default;
        }

        ushort format = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0;
        byte[] dataBytes = null;

        long length = reader.BaseStream.Length;
        while (reader.BaseStream.Position + 8 <= length) {
            string chunkId = ReadTag(reader);
            uint chunkSize = reader.ReadUInt32();
            long next = reader.BaseStream.Position + chunkSize + (chunkSize & 1);

            if (chunkId == "fmt ") {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                if (format == FormatExtensible)
                    format = bitsPerSample == 32 ? FormatFloat : FormatPcm;
            }
            else if (chunkId == "data") {
                dataBytes = reader.ReadBytes((int)chunkSize);
            }

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

    static short[] ConvertToPcm16(byte[] bytes, ushort format, ushort bits, string path) {
        switch (format) {
            case FormatPcm when bits == 16: {
                var outp = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, outp, 0, outp.Length * 2);
                return outp;
            }
            case FormatPcm when bits == 8: {
                var outp = new short[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                    outp[i] = (short)((bytes[i] - 128) << 8);
                return outp;
            }
            case FormatPcm when bits == 24: {
                int count = bytes.Length / 3;
                var outp = new short[count];
                for (int i = 0; i < count; i++) {
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
