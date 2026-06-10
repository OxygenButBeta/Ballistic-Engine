using System.Runtime.InteropServices;
using StbImageSharp;

namespace BallisticEngine.AssetPipeline;

// The only place in the engine that talks to StbImageSharp.
// LDR formats (png/jpg/...) decode to RGBA8; .hdr decodes to RGBA32F; .exr goes through SharpEXR.
public static class StbTextureDecoder {
    public static TextureData Decode(string path) {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".exr")
            return ExrDecoder.Decode(path);

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
