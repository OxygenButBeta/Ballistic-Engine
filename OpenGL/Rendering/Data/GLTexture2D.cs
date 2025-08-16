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
        GL.ActiveTexture((TextureUnit)TextureType);
        GL.BindTexture(TextureTarget.Texture2D, UID);
    }


    public override void Deactivate()
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

        if (TextureType == TextureType.Normal)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        }
        else
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
        }

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);


        PixelInternalFormat internalFormat = TextureType == TextureType.Diffuse
            ? PixelInternalFormat.SrgbAlpha
            : PixelInternalFormat.Rgba;

        GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, rawImage.Width, rawImage.Height,
            0, PixelFormat.Rgba, PixelType.UnsignedByte, rawImage.Data);

        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        isUploaded = true;
        
        GL.GetFloat((GetPName)All.MaxTextureMaxAnisotropyExt, out float maxAniso);
        GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)All.TextureMaxAnisotropyExt, maxAniso);
        
        Deactivate();
    }

    protected override void Import(ImageResult imageResult, TextureType textureType)
    {
        rawImage = imageResult;
        TextureType = textureType;
        GPUTexture();
    }
}