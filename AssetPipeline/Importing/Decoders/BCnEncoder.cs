namespace BallisticEngine.AssetPipeline;

// GPU block-compression encoder (the inverse of DdsDecoder), used at IMPORT time so the .btex
// artifact stores GPU-ready blocks instead of raw RGBA8. Uncompressed Bistro-scale content
// balloons to >12 GB of VRAM and crashes the driver; BC1/BC3/BC5 bring that down 4-8x with the
// same quality every shipping game ships with.
//
//   BC1 (DXT1):  RGB, 8 bytes / 4x4 block  -> 8:1 vs RGBA8.   Opaque & 1-bit-cutout color maps.
//   BC3 (DXT5):  RGBA, 16 bytes / block     -> 4:1.            Color maps with a real alpha channel.
//   BC5 (RGTC2): X,Y two-channel, 16 bytes  -> 4:1.            Tangent-space normal maps (R,G only).
//
// This is a "range-fit" encoder (stb_dxt class): per block, pick the two endpoints from the
// component-space bounding box of the 16 texels, then choose the best of the 4 (BC1) / 8 (BC4)
// interpolated levels per texel. Fast, no SIMD, deterministic — fine for an offline import step.
public static class BCnEncoder {
    // ---- Mip chain ----------------------------------------------------------

    // Number of mip levels for a WxH image down to 1x1 (standard full chain).
    public static int MipLevelCount(int width, int height) {
        int levels = 1;
        while (width > 1 || height > 1) {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            levels++;
        }
        return levels;
    }

    // Box-downsample one RGBA8 mip to the next. Averages 2x2 source texels (clamped at odd edges).
    public static byte[] DownsampleRgba8(byte[] src, int width, int height, out int dstW, out int dstH) {
        dstW = Math.Max(1, width >> 1);
        dstH = Math.Max(1, height >> 1);
        var dst = new byte[dstW * dstH * 4];

        for (int y = 0; y < dstH; y++) {
            int sy0 = y * 2;
            int sy1 = Math.Min(sy0 + 1, height - 1);
            for (int x = 0; x < dstW; x++) {
                int sx0 = x * 2;
                int sx1 = Math.Min(sx0 + 1, width - 1);
                for (int c = 0; c < 4; c++) {
                    int a = src[(sy0 * width + sx0) * 4 + c];
                    int b = src[(sy0 * width + sx1) * 4 + c];
                    int d = src[(sy1 * width + sx0) * 4 + c];
                    int e = src[(sy1 * width + sx1) * 4 + c];
                    dst[(y * dstW + x) * 4 + c] = (byte)((a + b + d + e + 2) / 4);
                }
            }
        }
        return dst;
    }

    // ---- Format selection ---------------------------------------------------

    // Whether this texture should be block-compressed, and in which format. RGBA32F (HDR) and any
    // image whose dimensions are not a multiple of 4 fall back to RGBA8 (the caller keeps the raw path).
    public static bool TryPickFormat(in TextureData data, TextureType type, out TextureFormat format) {
        format = TextureFormat.RGBA8;
        if (data.Format != TextureFormat.RGBA8)
            return false; // HDR float stays uncompressed
        if (data.Width % 4 != 0 || data.Height % 4 != 0 || data.Width < 4 || data.Height < 4)
            return false; // BC needs whole 4x4 blocks; tiny/odd textures stay raw

        if (type == TextureType.Normal) {
            format = TextureFormat.BC5; // dedicated 2-channel format — BC1/BC3 wreck normals
            return true;
        }

        // Color/data maps: BC3 only if the alpha channel actually carries information, else BC1.
        format = HasMeaningfulAlpha(data.Pixels) ? TextureFormat.BC3 : TextureFormat.BC1;
        return true;
    }

    static bool HasMeaningfulAlpha(byte[] rgba) {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255)
                return true;
        return false;
    }

    // ---- Encode -------------------------------------------------------------

    // Compresses one RGBA8 mip level to packed blocks in the given format. width/height must be
    // multiples of 4.
    public static byte[] EncodeLevel(byte[] rgba, int width, int height, TextureFormat format) {
        int blocksWide = width / 4;
        int blocksHigh = height / 4;
        int blockBytes = format.BlockBytes();
        var output = new byte[blocksWide * blocksHigh * blockBytes];

        Span<byte> texels = stackalloc byte[64]; // 4x4 RGBA

        for (int by = 0; by < blocksHigh; by++) {
            for (int bx = 0; bx < blocksWide; bx++) {
                GatherBlock(rgba, width, bx, by, texels);
                int o = (by * blocksWide + bx) * blockBytes;
                Span<byte> block = output.AsSpan(o, blockBytes);

                switch (format) {
                    case TextureFormat.BC1:
                        EncodeColorBlock(texels, block, opaque: true);
                        break;
                    case TextureFormat.BC3:
                        EncodeAlphaBlock(texels, channel: 3, block); // first 8 bytes: alpha
                        EncodeColorBlock(texels, block[8..], opaque: true);
                        break;
                    case TextureFormat.BC5:
                        EncodeAlphaBlock(texels, channel: 0, block);     // X -> first 8 bytes
                        EncodeAlphaBlock(texels, channel: 1, block[8..]); // Y -> next 8 bytes
                        break;
                }
            }
        }
        return output;
    }

    // Copy a 4x4 region (no clipping needed: dims are multiples of 4) into a tight RGBA buffer.
    static void GatherBlock(byte[] rgba, int width, int bx, int by, Span<byte> texels) {
        for (int py = 0; py < 4; py++) {
            int srcRow = ((by * 4 + py) * width + bx * 4) * 4;
            rgba.AsSpan(srcRow, 16).CopyTo(texels.Slice(py * 16, 16));
        }
    }

    // ---- BC1 color block (also the color half of BC3) -----------------------

    static void EncodeColorBlock(ReadOnlySpan<byte> texels, Span<byte> dst, bool opaque) {
        // Bounding box of the block's colors in RGB.
        int rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
        for (int i = 0; i < 16; i++) {
            int r = texels[i * 4], g = texels[i * 4 + 1], b = texels[i * 4 + 2];
            if (r < rMin) rMin = r; if (r > rMax) rMax = r;
            if (g < gMin) gMin = g; if (g > gMax) gMax = g;
            if (b < bMin) bMin = b; if (b > bMax) bMax = b;
        }

        // Inset the box slightly (stb_dxt trick): pulls endpoints off the extremes for a better fit.
        int insetR = (rMax - rMin) >> 4, insetG = (gMax - gMin) >> 4, insetB = (bMax - bMin) >> 4;
        rMin = Math.Min(rMin + insetR, 255); gMin = Math.Min(gMin + insetG, 255); bMin = Math.Min(bMin + insetB, 255);
        rMax = Math.Max(rMax - insetR, 0); gMax = Math.Max(gMax - insetG, 0); bMax = Math.Max(bMax - insetB, 0);

        ushort c0 = Pack565(rMax, gMax, bMax);
        ushort c1 = Pack565(rMin, gMin, bMin);

        // BC1 four-color mode requires c0 > c1 (c0 <= c1 would select the 3-color+punchthrough mode).
        if (c0 < c1) (c0, c1) = (c1, c0);
        if (c0 == c1) {
            // Flat block: identical endpoints, all indices 0. Avoid a degenerate palette.
            dst[0] = (byte)(c0 & 0xFF); dst[1] = (byte)(c0 >> 8);
            dst[2] = (byte)(c1 & 0xFF); dst[3] = (byte)(c1 >> 8);
            dst[4] = dst[5] = dst[6] = dst[7] = 0;
            return;
        }

        // Build the 4-entry palette exactly as the decoder will (so what we choose is what gets shown).
        Span<int> pr = stackalloc int[4];
        Span<int> pg = stackalloc int[4];
        Span<int> pb = stackalloc int[4];
        Unpack565(c0, out pr[0], out pg[0], out pb[0]);
        Unpack565(c1, out pr[1], out pg[1], out pb[1]);
        pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
        pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;

        uint bits = 0;
        for (int i = 0; i < 16; i++) {
            int r = texels[i * 4], g = texels[i * 4 + 1], b = texels[i * 4 + 2];
            int best = 0, bestErr = int.MaxValue;
            for (int p = 0; p < 4; p++) {
                int dr = r - pr[p], dg = g - pg[p], db = b - pb[p];
                int err = dr * dr + dg * dg + db * db;
                if (err < bestErr) { bestErr = err; best = p; }
            }
            bits |= (uint)best << (2 * i);
        }

        dst[0] = (byte)(c0 & 0xFF); dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)(c1 & 0xFF); dst[3] = (byte)(c1 >> 8);
        dst[4] = (byte)(bits & 0xFF); dst[5] = (byte)((bits >> 8) & 0xFF);
        dst[6] = (byte)((bits >> 16) & 0xFF); dst[7] = (byte)((bits >> 24) & 0xFF);
    }

    // ---- BC4 single-channel block (alpha for BC3; X or Y for BC5) ------------

    static void EncodeAlphaBlock(ReadOnlySpan<byte> texels, int channel, Span<byte> dst) {
        int lo = 255, hi = 0;
        for (int i = 0; i < 16; i++) {
            int v = texels[i * 4 + channel];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }

        // Eight-value (a0 > a1) interpolation mode gives the best fidelity; we always emit it.
        byte a0 = (byte)hi, a1 = (byte)lo;
        if (a0 == a1) {
            // Flat channel: endpoints equal -> all indices 0. (a0 > a1 won't hold, but with equal
            // endpoints every interpolated value equals a0 so index 0 is exact regardless of mode.)
            dst[0] = a0; dst[1] = a1;
            dst[2] = dst[3] = dst[4] = dst[5] = dst[6] = dst[7] = 0;
            return;
        }

        Span<int> values = stackalloc int[8];
        values[0] = a0;
        values[1] = a1;
        for (int i = 1; i < 7; i++)
            values[1 + i] = ((7 - i) * a0 + i * a1) / 7;

        ulong bits = 0;
        for (int i = 0; i < 16; i++) {
            int v = texels[i * 4 + channel];
            int best = 0, bestErr = int.MaxValue;
            for (int p = 0; p < 8; p++) {
                int d = v - values[p];
                int err = d * d;
                if (err < bestErr) { bestErr = err; best = p; }
            }
            bits |= (ulong)best << (3 * i);
        }

        dst[0] = a0;
        dst[1] = a1;
        for (int i = 0; i < 6; i++)
            dst[2 + i] = (byte)((bits >> (8 * i)) & 0xFF);
    }

    // ---- 565 helpers --------------------------------------------------------

    static ushort Pack565(int r, int g, int b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    static void Unpack565(ushort v, out int r, out int g, out int b) {
        int r5 = (v >> 11) & 0x1F, g6 = (v >> 5) & 0x3F, b5 = v & 0x1F;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}
