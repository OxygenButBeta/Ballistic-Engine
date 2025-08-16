using BallisticEngine.Shared.Runtime_Set;
using StbImageSharp;

namespace BallisticEngine;

public abstract class Texture : BObject, IDisposable, ISharedResource
{
    public ResourceIdentity Identity { get; private set; }

    protected Texture()
    {
        Debugging.SystemLog("Texture Created ", SystemLogPriority.Critical);
    }

    public abstract int UID { get; protected set; }
    public abstract void Activate();
    public abstract void Deactivate();
    public abstract void Dispose();

    protected abstract void Import(ImageResult imageResult, TextureType textureType);

    public static TTargetTexture ImportFromFile<TTargetTexture>(string path, TextureType textureType)
        where TTargetTexture : Texture, new()
    {
        if (SharedResources<TTargetTexture>.TryGetResource(path+textureType, out TTargetTexture sharedTexture))
        {
           return sharedTexture;
        }

        TTargetTexture textureInstance = new();

        ImageResult texture = ImageResult.FromStream(
            File.OpenRead(path),
            ColorComponents.RedGreenBlueAlpha);

        if (texture is null)
            throw new FileNotFoundException($"Texture file not found at path: {path}");


        textureInstance.Import(texture, textureType);
        textureInstance.Identity = path+textureType;
       SharedResources<TTargetTexture>.AddResource(textureInstance);
        return textureInstance;
    }
}