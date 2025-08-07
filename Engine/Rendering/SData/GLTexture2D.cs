using BallisticEngine.Shared.Runtime_Set;
using OpenTK.Graphics.OpenGL;
using StbImageSharp;

namespace BallisticEngine;

public sealed class GLTexture2D : Texture2D
{
    public override int UID { get; protected set; }
    ImageResult rawImage;
    TextureType TextureType;
    bool isUploaded;

    public override void Activate()
    {
        if (!isUploaded)
        {
            GPUTexture();
            isUploaded = true;
        }

        GL.ActiveTexture((TextureUnit)TextureType.Diffuse);
        GL.BindTexture(TextureTarget.Texture2D, UID);
    }


    public override void Deselect()
    {
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public override void Dispose()
    {
        if (isUploaded)
            GL.DeleteTexture(UID);
    }

    void GPUTexture()
    {
        
        UID = GL.GenTexture();

        GL.ActiveTexture((TextureUnit)TextureType);
        GL.BindTexture(TextureTarget.Texture2D, UID);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);


        StbImage.stbi_set_flip_vertically_on_load(1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, rawImage.Width, rawImage.Height,
            0, PixelFormat.Rgba, PixelType.UnsignedByte, rawImage.Data);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        isUploaded = true;
        Deselect();
    }

    protected override void Import(ImageResult imageResult, TextureType textureType)
    {
        rawImage = imageResult;
        TextureType = textureType;
        GPUTexture();
    }
}