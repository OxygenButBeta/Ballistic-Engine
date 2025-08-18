using OpenTK.Mathematics;
using StbImageSharp;

namespace BallisticEngine;

// A marker class for 3D textures.
public abstract class Texture3D : Texture
{
    public Vector3 skyAmbient = Vector3.Zero;
    public static TTexture3D ImportCubeMapFromFile<TTexture3D>(string[] paths)
        where TTexture3D : Texture3D, new()
    {
        List<ImageResult> imageResults = new();
        TTexture3D textureInstance = new();

        foreach (var path in paths)
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