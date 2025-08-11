using System;
using OpenTK.Graphics.OpenGL;
using StbImageSharp;

namespace BallisticEngine;

public sealed class GLTexture3D : Texture3D
{
    public override int UID { get; protected set; }
    bool isUploaded;

    ImageResult[] rawImages = new ImageResult[6];

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

    public void Import(ImageResult[] images)
    {
        if (images == null || images.Length != 6)
            throw new ArgumentException("Cubemap requires exactly 6 images");

        rawImages = images;

        GPUTexture();
    }

    void GPUTexture()
    {
        UID = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, UID);

        StbImage.stbi_set_flip_vertically_on_load(0); 

        for (var i = 0; i < 6; i++)
        {
            ImageResult img = rawImages[i];

            PixelInternalFormat internalFormat = PixelInternalFormat.SrgbAlpha; 
            PixelFormat pixelFormat = PixelFormat.Rgba;

            GL.TexImage2D(CubemapFaces[i], 0, internalFormat, img.Width, img.Height,
                0, pixelFormat, PixelType.UnsignedByte, img.Data);
        }

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
}
