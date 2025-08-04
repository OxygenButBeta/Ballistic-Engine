using OpenTK.Graphics.OpenGL;
using StbImageSharp;

namespace BallisticEngine;

public sealed class Texture2D : BObject, IDisposable {
    internal int ID { get; private set; }
    bool _isLoaded;

    public void Bind() {
        if (!_isLoaded) {
            gpuTexture();
            _isLoaded = true;
        }

        GL.BindTexture(TextureTarget.Texture2D, ID);
    }

    public void UnBind() {
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose() {
        if (_isLoaded)
            GL.DeleteTexture(ID);
    }

    private void gpuTexture() {
        ID = GL.GenTexture();

        using FileStream stream = File.OpenRead("D:\\Tex.png"); // Replace with your texture file path
        ImageResult? texture = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, ID);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texture.Width, texture.Height,
            0, PixelFormat.Rgba, PixelType.UnsignedByte, texture.Data);

        UnBind();
    }
}