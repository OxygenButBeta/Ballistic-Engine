using BallisticEngine.Shared.Runtime_Set;
using StbImageSharp;

namespace BallisticEngine;

public abstract class Texture : BObject, IDisposable {
    protected Texture() {
        Debugging.SystemLog("Texture Created ", SystemLogPriority.Critical);
    }

    public abstract int UID { get; protected set; }
    public abstract void Activate();
    public abstract void Deactivate();
    public abstract void Dispose();

    protected abstract void Import(ImageResult imageResult, TextureType textureType);

    public static TTargetTexture ImportFromFile<TTargetTexture>(string path, TextureType textureType)
        where TTargetTexture : Texture, new() {
        //Check if the texture is already loaded in the cache
        if (RuntimeCache<string, TTargetTexture>.TryGetValue(path, out TTargetTexture cachedTexture)) {
            Debugging.SystemLog("Texture loaded from cache: " + path);
            return cachedTexture;
        }

        TTargetTexture textureInstance = new TTargetTexture();

        if (RuntimeCache<string, ImageResult>.TryGetValue(path, out ImageResult cachedImage)) {
            textureInstance.Import(cachedImage, textureType);
            Debugging.SystemLog("Raw Texture loaded from cache: " + path);
            return textureInstance;
        }

        ImageResult texture = ImageResult.FromStream(
            File.OpenRead(path),
            ColorComponents.RedGreenBlueAlpha);

        if (texture is null)
            throw new FileNotFoundException($"Texture file not found at path: {path}");

        RuntimeCache<string, ImageResult>.Add(path, texture);

        textureInstance.Import(texture, textureType);
        RuntimeCache<string, TTargetTexture>.Add(path, textureInstance);
        return textureInstance;
    }
}