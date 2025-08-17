using OpenTK.Mathematics;
using StbImageSharp;

namespace BallisticEngine;

// A marker class for 3D textures.
public abstract class Texture3D : Texture
{
    public Vector3 skyAmbient = Vector3.Zero;
    public static TTexture3D ImportFromFile<TTexture3D>(string[] paths)
        where TTexture3D : Texture3D, new()
    {
        List<ImageResult> imageResults = new List<ImageResult>();
        TTexture3D textureInstance = new TTexture3D();

        foreach (string path in paths)
        {
            ImageResult texture = ImageResult.FromStream(
                File.OpenRead(path),
                ColorComponents.RedGreenBlueAlpha);

            if (texture is null)
                throw new FileNotFoundException($"Texture file not found at path: {path}");

            imageResults.Add(texture);
        }

        textureInstance.ImportFaces(imageResults);
        return textureInstance;
    }

    protected abstract void ImportFaces(IReadOnlyList<ImageResult> imageResult);
}