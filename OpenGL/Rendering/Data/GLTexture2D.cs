using OpenTK.Graphics.OpenGL;

namespace BallisticEngine;

public sealed class GLTexture2D : Texture2D {
    public override int UID { get; protected set; }
    bool isUploaded;

    public override void Activate() {
        GL.ActiveTexture(TextureUnit.Texture0 + (int)Type);
        GL.BindTexture(TextureTarget.Texture2D, UID);
    }

    public override void Deactivate() {
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public override void Dispose() {
        if (isUploaded)
            GL.DeleteTexture(UID);
    }

    protected internal override void Upload(in TextureData data, TextureType type) {
        Type = type;

        UID = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0 + (int)Type);
        GL.BindTexture(TextureTarget.Texture2D, UID);

        if (Type == TextureType.Normal) {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        }
        else {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
        }

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        if (data.Format == TextureFormat.RGBA32F) {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, data.Width, data.Height,
                0, PixelFormat.Rgba, PixelType.Float, data.Pixels);
        }
        else {
            PixelInternalFormat internalFormat = Type == TextureType.Diffuse
                ? PixelInternalFormat.SrgbAlpha
                : PixelInternalFormat.Rgba;

            if (Type is TextureType.Metallic or TextureType.Roughness or TextureType.AO) {
                internalFormat = PixelInternalFormat.R8;
            }
            else if (Type == TextureType.Normal) {
                internalFormat = PixelInternalFormat.Rgb8;
            }

            GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, data.Width, data.Height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, data.Pixels);
        }

        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        isUploaded = true;

        GL.GetFloat((GetPName)All.MaxTextureMaxAnisotropyExt, out float maxAniso);
        GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)All.TextureMaxAnisotropyExt, maxAniso);

        Deactivate();
    }
}
