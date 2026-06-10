using StbImageSharp;

namespace BallisticEngine.AssetPipeline;

// The only place in the engine that talks to StbImageSharp.
public static class StbTextureDecoder {
    public static TextureData Decode(string path) {
        using FileStream stream = File.OpenRead(path);
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        if (image is null)
            throw new IOException($"Failed to decode image '{path}'.");

        return new TextureData(image.Width, image.Height, TextureFormat.RGBA8, image.Data);
    }
}
