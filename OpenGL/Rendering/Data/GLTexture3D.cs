using System;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using StbImageSharp;

namespace BallisticEngine;

public sealed class GLTexture3D : Texture3D
{
    public override int UID { get; protected set; }
    bool isUploaded;

    ImageResult[] rawImages = new ImageResult[6];
    int totalPixels;

    static readonly TextureTarget[] CubemapFaces = [
        TextureTarget.TextureCubeMapPositiveX,
        TextureTarget.TextureCubeMapNegativeX,
        TextureTarget.TextureCubeMapPositiveY,
        TextureTarget.TextureCubeMapNegativeY,
        TextureTarget.TextureCubeMapPositiveZ,
        TextureTarget.TextureCubeMapNegativeZ
    ];

    public override void Activate()
    {
        if (!isUploaded)
        {
            GPUTexture();
            isUploaded = true;
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.TextureCubeMap, UID);
    }

    public override void Deactivate()
    {
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
    }

    public override void Dispose()
    {
        if (isUploaded)
            GL.DeleteTexture(UID);
    }

    void GPUTexture()
    {
        UID = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, UID);

        for (var i = 0; i < 6; i++)
        {
            ImageResult img = rawImages[i];

            PixelInternalFormat internalFormat = PixelInternalFormat.Srgb; 
            PixelFormat pixelFormat = PixelFormat.Rgba;

            for (int y = 0; y < img.Height; y++)
            {
                for (int x = 0; x < img.Width; x++)
                {
                    int index = (y * img.Width + x) * 4; // RGBA
                    float r = img.Data[index + 0] / 255f;
                    float g = img.Data[index + 1] / 255f;
                    float b = img.Data[index + 2] / 255f;

                    skyAmbient += new Vector3(r, g, b);
                    totalPixels++;
                }
            }
            
            GL.TexImage2D(CubemapFaces[i], 0, internalFormat, img.Width, img.Height,
                0, pixelFormat, PixelType.UnsignedByte, img.Data);
        }
        skyAmbient /= totalPixels;
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);

        isUploaded = true;

        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
    }

    protected override void Import(ImageResult imageResult, TextureType textureType)
    {
        throw new NotImplementedException("Use Import(ImageResult[] images) for cubemap");
    }

    protected override void ImportFaces(IReadOnlyList<ImageResult> imageResults)
    {
        if (imageResults == null || imageResults.Count != 6)
            throw new ArgumentException("Cubemap requires exactly 6 images in the order: +X, -X, +Y, -Y, +Z, -Z");

        rawImages = imageResults.ToArray();

        GPUTexture(); 
        
    }
}
