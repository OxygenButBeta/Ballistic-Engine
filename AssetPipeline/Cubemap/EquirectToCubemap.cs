using System.Runtime.InteropServices;

namespace BallisticEngine.AssetPipeline;

// CPU conversion of an equirectangular panorama (typically .hdr/.exr) into 6 cubemap faces,
// in the renderer's face order: +X, -X, +Y, -Y, +Z, -Z. Output format matches the input
// (RGBA8 or RGBA32F). Bilinear sampling.
public static class EquirectToCubemap {
    public static TextureData[] Convert(in TextureData equirect, int faceSize) {
        faceSize = Math.Clamp(faceSize, 16, 4096);
        var faces = new TextureData[6];

        for (var face = 0; face < 6; face++)
            faces[face] = RenderFace(in equirect, face, faceSize);

        return faces;
    }

    static TextureData RenderFace(in TextureData src, int face, int size) {
        var isFloat = src.Format == TextureFormat.RGBA32F;
        var bytesPerPixel = isFloat ? 16 : 4;
        var pixels = new byte[size * size * bytesPerPixel];

        Span<float> srcFloats = isFloat ? MemoryMarshal.Cast<byte, float>(src.Pixels) : default;
        Span<float> dstFloats = isFloat ? MemoryMarshal.Cast<byte, float>(pixels) : default;

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                // Face texel -> direction -> equirect uv.
                var uc = (x + 0.5f) / size * 2f - 1f;
                var vc = (y + 0.5f) / size * 2f - 1f;
                Vector3 dir = FaceDirection(face, uc, vc).Normalized();

                var u = MathF.Atan2(dir.Z, dir.X) / (2f * MathF.PI) + 0.5f;
                var v = 0.5f - MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) / MathF.PI;

                var fx = Math.Clamp(u * src.Width - 0.5f, 0, src.Width - 1.001f);
                var fy = Math.Clamp(v * src.Height - 0.5f, 0, src.Height - 1.001f);
                var x0 = (int)fx;
                var y0 = (int)fy;
                var tx = fx - x0;
                var ty = fy - y0;
                var x1 = Math.Min(x0 + 1, src.Width - 1);
                var y1 = Math.Min(y0 + 1, src.Height - 1);

                var dst = (y * size + x) * 4;
                for (var c = 0; c < 4; c++) {
                    if (isFloat) {
                        float s00 = srcFloats[(y0 * src.Width + x0) * 4 + c];
                        float s10 = srcFloats[(y0 * src.Width + x1) * 4 + c];
                        float s01 = srcFloats[(y1 * src.Width + x0) * 4 + c];
                        float s11 = srcFloats[(y1 * src.Width + x1) * 4 + c];
                        var value = Lerp(Lerp(s00, s10, tx), Lerp(s01, s11, tx), ty);
                        // The cubemap uploads as half-float: radiance above fp16 max (~65504,
                        // e.g. the sun disc) becomes Inf and tonemaps to NaN/black holes. Clamp.
                        dstFloats[dst + c] = float.IsFinite(value) ? Math.Min(value, 60000f) : 60000f;
                    }
                    else {
                        float s00 = src.Pixels[(y0 * src.Width + x0) * 4 + c];
                        float s10 = src.Pixels[(y0 * src.Width + x1) * 4 + c];
                        float s01 = src.Pixels[(y1 * src.Width + x0) * 4 + c];
                        float s11 = src.Pixels[(y1 * src.Width + x1) * 4 + c];
                        pixels[dst + c] = (byte)Math.Clamp(
                            Lerp(Lerp(s00, s10, tx), Lerp(s01, s11, tx), ty), 0f, 255f);
                    }
                }
            }
        }

        return new TextureData(size, size, src.Format, pixels);
    }

    // GL cubemap face conventions, order +X, -X, +Y, -Y, +Z, -Z.
    static Vector3 FaceDirection(int face, float u, float v) => face switch {
        0 => new Vector3(1f, -v, -u),
        1 => new Vector3(-1f, -v, u),
        2 => new Vector3(u, 1f, v),
        3 => new Vector3(u, -1f, -v),
        4 => new Vector3(u, -v, 1f),
        _ => new Vector3(-u, -v, -1f),
    };

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
