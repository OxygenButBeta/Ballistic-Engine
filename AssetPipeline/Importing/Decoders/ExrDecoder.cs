using ImageMagick;

namespace BallisticEngine.AssetPipeline;

public static class ExrDecoder {
    public static TextureData Decode(string path) {
        using var image = new MagickImage(path);

        var width = (int)image.Width;
        var height = (int)image.Height;
        var channels = (int)image.ChannelCount;

        using IPixelCollection<float> pixelCollection = image.GetPixels();
        var source = pixelCollection.ToArray();
        if (source is null)
            throw new IOException($"Failed to decode EXR '{path}'.");

        var inv = 1f / Quantum.Max;
        var floats = new float[width * height * 4];

        for (int i = 0, p = 0; i < width * height; i++, p += channels) {
            floats[i * 4 + 0] = source[p] * inv;
            floats[i * 4 + 1] = source[p + (channels > 1 ? 1 : 0)] * inv;
            floats[i * 4 + 2] = source[p + (channels > 2 ? 2 : 0)] * inv;
            floats[i * 4 + 3] = channels > 3 ? source[p + 3] * inv : 1f;
        }

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(floats).ToArray();
        return new TextureData(width, height, TextureFormat.RGBA32F, bytes);
    }
}
