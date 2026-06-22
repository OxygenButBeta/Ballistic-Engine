using System.Runtime.InteropServices;
using StbImageSharp;

namespace BallisticEngine.AssetPipeline;

public static class StbTextureDecoder {
    public static TextureData Decode(string path) {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".exr")
            return ExrDecoder.Decode(path);

        if (extension == ".dds")
            return DdsDecoder.Decode(path);

        using FileStream stream = File.OpenRead(path);

        if (extension == ".hdr") {
            ImageResultFloat hdr = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (hdr is null)
                throw new IOException($"Failed to decode HDR image '{path}'.");

            var bytes = MemoryMarshal.AsBytes<float>(hdr.Data).ToArray();
            return new TextureData(hdr.Width, hdr.Height, TextureFormat.RGBA32F, bytes);
        }

        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (image is null)
            throw new IOException($"Failed to decode image '{path}'.");

        return new TextureData(image.Width, image.Height, TextureFormat.RGBA8, image.Data);
    }
}
