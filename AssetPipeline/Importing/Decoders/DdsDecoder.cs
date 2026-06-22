namespace BallisticEngine.AssetPipeline;

public static class DdsDecoder {
    const uint DdsMagic = 0x20534444;
    const uint FourCcDx10 = 0x30315844;

    const uint PfFourCc = 0x4;
    const uint PfRgb = 0x40;
    const uint PfAlphaPixels = 0x1;
    const uint PfLuminance = 0x20000;

    const uint Caps2Cubemap = 0x200;

    enum BlockFormat { None, BC1, BC2, BC3, BC4, BC5 }

    public static TextureData Decode(string path) {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != DdsMagic)
            throw new InvalidDataException($"'{path}' is not a DDS file (bad magic).");
        if (reader.ReadUInt32() != 124)
            throw new InvalidDataException($"'{path}' has a malformed DDS header.");

        reader.ReadUInt32();
        var height = reader.ReadInt32();
        var width = reader.ReadInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        for (var i = 0; i < 11; i++)
            reader.ReadUInt32();

        if (reader.ReadUInt32() != 32)
            throw new InvalidDataException($"'{path}' has a malformed DDS pixel format.");
        var pfFlags = reader.ReadUInt32();
        var fourCc = reader.ReadUInt32();
        var bitCount = (int)reader.ReadUInt32();
        var rMask = reader.ReadUInt32();
        var gMask = reader.ReadUInt32();
        var bMask = reader.ReadUInt32();
        var aMask = reader.ReadUInt32();

        reader.ReadUInt32();
        var caps2 = reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        BlockFormat block = BlockFormat.None;
        var dx10 = false;

        if ((pfFlags & PfFourCc) != 0) {
            if (fourCc == FourCcDx10) {
                dx10 = true;
                var dxgiFormat = reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                (block, bitCount, rMask, gMask, bMask, aMask) = FromDxgi(path, dxgiFormat);
            }
            else {
                block = fourCc switch {
                    0x31545844 => BlockFormat.BC1,
                    0x32545844 or 0x33545844 => BlockFormat.BC2,
                    0x34545844 or 0x35545844 => BlockFormat.BC3,
                    0x31495441 or 0x55344342 => BlockFormat.BC4,
                    0x32495441 or 0x55354342 => BlockFormat.BC5,
                    _ => throw new InvalidDataException(
                        $"'{path}': unsupported DDS fourCC '{FourCcText(fourCc)}'."),
                };
            }
        }

        if ((caps2 & Caps2Cubemap) != 0)
            Debugging.LogWarning($"'{path}' is a cubemap DDS; only the first face is decoded.");

        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"'{path}' has invalid dimensions {width}x{height}.");

        var pixels = block != BlockFormat.None
            ? DecodeBlocks(reader, width, height, block, path)
            : DecodeUncompressed(reader, width, height, bitCount, pfFlags, dx10,
                rMask, gMask, bMask, aMask, path);

        return new TextureData(width, height, TextureFormat.RGBA8, pixels);
    }

    static (BlockFormat, int, uint, uint, uint, uint) FromDxgi(string path, uint dxgiFormat) =>
        dxgiFormat switch {
            70 or 71 or 72 => (BlockFormat.BC1, 0, 0u, 0u, 0u, 0u),
            73 or 74 or 75 => (BlockFormat.BC2, 0, 0u, 0u, 0u, 0u),
            76 or 77 or 78 => (BlockFormat.BC3, 0, 0u, 0u, 0u, 0u),
            79 or 80 or 81 => (BlockFormat.BC4, 0, 0u, 0u, 0u, 0u),
            82 or 83 or 84 => (BlockFormat.BC5, 0, 0u, 0u, 0u, 0u),
            27 or 28 or 29 => (BlockFormat.None, 32, 0x000000FFu, 0x0000FF00u, 0x00FF0000u, 0xFF000000u),
            87 or 88 or 91 or 93 => (BlockFormat.None, 32, 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u),
            94 or 95 or 96 => throw new InvalidDataException(
                $"'{path}': BC6H DDS textures are not supported yet; re-export as BC1-BC5 or PNG/TGA."),
            97 or 98 or 99 => throw new InvalidDataException(
                $"'{path}': BC7 DDS textures are not supported yet; re-export as BC1-BC5 or PNG/TGA."),
            _ => throw new InvalidDataException($"'{path}': unsupported DXGI format {dxgiFormat}."),
        };

    static byte[] DecodeUncompressed(BinaryReader reader, int width, int height, int bitCount,
        uint pfFlags, bool dx10, uint rMask, uint gMask, uint bMask, uint aMask, string path) {
        if (!dx10) {
            var isRgb = (pfFlags & PfRgb) != 0;
            var isLuminance = (pfFlags & PfLuminance) != 0;
            if (!isRgb && !isLuminance)
                throw new InvalidDataException($"'{path}': unsupported uncompressed DDS pixel format.");

            if (isLuminance) {
                gMask = bMask = rMask;
                if ((pfFlags & PfAlphaPixels) == 0)
                    aMask = 0;
            }
        }

        if (bitCount is not (8 or 16 or 24 or 32))
            throw new InvalidDataException($"'{path}': unsupported DDS bit depth {bitCount}.");

        var bytesPerPixel = bitCount / 8;
        var source = reader.ReadBytes(width * height * bytesPerPixel);
        if (source.Length < width * height * bytesPerPixel)
            throw new InvalidDataException($"'{path}': DDS pixel data is truncated.");

        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) {
            uint raw = 0;
            for (var b = 0; b < bytesPerPixel; b++)
                raw |= (uint)source[i * bytesPerPixel + b] << (8 * b);

            var o = i * 4;
            pixels[o] = Channel(raw, rMask, 0);
            pixels[o + 1] = Channel(raw, gMask, 0);
            pixels[o + 2] = Channel(raw, bMask, 0);
            pixels[o + 3] = Channel(raw, aMask, 255);
        }

        return pixels;
    }

    static byte Channel(uint pixel, uint mask, byte whenAbsent) {
        if (mask == 0)
            return whenAbsent;

        var shift = BitOperations.TrailingZeroCount(mask);
        var bits = BitOperations.PopCount(mask);
        var value = (pixel & mask) >> shift;
        var max = (1u << bits) - 1;
        return (byte)(value * 255 / max);
    }

    static byte[] DecodeBlocks(BinaryReader reader, int width, int height, BlockFormat format, string path) {
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        var blockSize = format is BlockFormat.BC1 or BlockFormat.BC4 ? 8 : 16;

        var data = reader.ReadBytes(blocksWide * blocksHigh * blockSize);
        if (data.Length < blocksWide * blocksHigh * blockSize)
            throw new InvalidDataException($"'{path}': DDS block data is truncated.");

        var pixels = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];

        for (var by = 0; by < blocksHigh; by++) {
            for (var bx = 0; bx < blocksWide; bx++) {
                ReadOnlySpan<byte> src = data.AsSpan((by * blocksWide + bx) * blockSize, blockSize);

                switch (format) {
                    case BlockFormat.BC1:
                        DecodeColorBlock(src, block, allowPunchThrough: true);
                        break;
                    case BlockFormat.BC2:
                        DecodeColorBlock(src[8..], block, allowPunchThrough: false);
                        ApplyBc2Alpha(src, block);
                        break;
                    case BlockFormat.BC3:
                        DecodeColorBlock(src[8..], block, allowPunchThrough: false);
                        ApplyBc4Channel(src, block, channel: 3);
                        break;
                    case BlockFormat.BC4:
                        FillGray(block);
                        ApplyBc4Channel(src, block, channel: 0, replicateRgb: true);
                        break;
                    case BlockFormat.BC5:
                        FillGray(block);
                        ApplyBc4Channel(src, block, channel: 0);
                        ApplyBc4Channel(src[8..], block, channel: 1);
                        ReconstructNormalZ(block);
                        break;
                }

                for (var py = 0; py < 4; py++) {
                    var y = by * 4 + py;
                    if (y >= height)
                        break;
                    for (var px = 0; px < 4; px++) {
                        var x = bx * 4 + px;
                        if (x >= width)
                            break;
                        var src4 = (py * 4 + px) * 4;
                        var dst = (y * width + x) * 4;
                        pixels[dst] = block[src4];
                        pixels[dst + 1] = block[src4 + 1];
                        pixels[dst + 2] = block[src4 + 2];
                        pixels[dst + 3] = block[src4 + 3];
                    }
                }
            }
        }

        return pixels;
    }

    static void DecodeColorBlock(ReadOnlySpan<byte> src, Span<byte> rgba, bool allowPunchThrough) {
        var c0 = (ushort)(src[0] | src[1] << 8);
        var c1 = (ushort)(src[2] | src[3] << 8);

        Span<byte> colors = stackalloc byte[16];
        Expand565(c0, colors);
        Expand565(c1, colors[4..]);

        if (!allowPunchThrough || c0 > c1) {
            for (var c = 0; c < 3; c++) {
                colors[8 + c] = (byte)((2 * colors[c] + colors[4 + c]) / 3);
                colors[12 + c] = (byte)((colors[c] + 2 * colors[4 + c]) / 3);
            }
            colors[11] = 255;
            colors[15] = 255;
        }
        else {
            for (var c = 0; c < 3; c++) {
                colors[8 + c] = (byte)((colors[c] + colors[4 + c]) / 2);
                colors[12 + c] = 0;
            }
            colors[11] = 255;
            colors[15] = 0;
        }

        var bits = (uint)(src[4] | src[5] << 8 | src[6] << 16 | src[7] << 24);
        for (var i = 0; i < 16; i++) {
            var index = (int)((bits >> (2 * i)) & 3) * 4;
            var o = i * 4;
            rgba[o] = colors[index];
            rgba[o + 1] = colors[index + 1];
            rgba[o + 2] = colors[index + 2];
            rgba[o + 3] = colors[index + 3];
        }
    }

    static void Expand565(ushort value, Span<byte> rgb) {
        var r = (value >> 11) & 0x1F;
        var g = (value >> 5) & 0x3F;
        var b = value & 0x1F;
        rgb[0] = (byte)(r << 3 | r >> 2);
        rgb[1] = (byte)(g << 2 | g >> 4);
        rgb[2] = (byte)(b << 3 | b >> 2);
        rgb[3] = 255;
    }

    static void ApplyBc2Alpha(ReadOnlySpan<byte> src, Span<byte> rgba) {
        for (var i = 0; i < 8; i++) {
            rgba[i * 8 + 3] = (byte)((src[i] & 0xF) * 17);
            rgba[i * 8 + 7] = (byte)((src[i] >> 4) * 17);
        }
    }

    static void ApplyBc4Channel(ReadOnlySpan<byte> src, Span<byte> rgba, int channel,
        bool replicateRgb = false) {
        var a0 = src[0];
        var a1 = src[1];

        Span<byte> values = stackalloc byte[8];
        values[0] = a0;
        values[1] = a1;
        if (a0 > a1) {
            for (var i = 1; i < 7; i++)
                values[1 + i] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }
        else {
            for (var i = 1; i < 5; i++)
                values[1 + i] = (byte)(((5 - i) * a0 + i * a1) / 5);
            values[6] = 0;
            values[7] = 255;
        }

        ulong bits = 0;
        for (var i = 0; i < 6; i++)
            bits |= (ulong)src[2 + i] << (8 * i);

        for (var i = 0; i < 16; i++) {
            var value = values[(int)((bits >> (3 * i)) & 7)];
            var o = i * 4;
            if (replicateRgb) {
                rgba[o] = value;
                rgba[o + 1] = value;
                rgba[o + 2] = value;
            }
            else {
                rgba[o + channel] = value;
            }
        }
    }

    static void FillGray(Span<byte> rgba) {
        for (var i = 0; i < 16; i++) {
            var o = i * 4;
            rgba[o] = 0;
            rgba[o + 1] = 0;
            rgba[o + 2] = 0;
            rgba[o + 3] = 255;
        }
    }

    static void ReconstructNormalZ(Span<byte> rgba) {
        for (var i = 0; i < 16; i++) {
            var o = i * 4;
            var x = rgba[o] / 255f * 2f - 1f;
            var y = rgba[o + 1] / 255f * 2f - 1f;
            var z = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y));
            rgba[o + 2] = (byte)((z * 0.5f + 0.5f) * 255f);
        }
    }

    static string FourCcText(uint fourCc) {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++) {
            var c = (char)((fourCc >> (8 * i)) & 0xFF);
            chars[i] = char.IsControl(c) ? '?' : c;
        }
        return new string(chars);
    }
}
