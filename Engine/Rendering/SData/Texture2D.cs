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
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, ID);
    }

    public void UnBind() {
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose() {
        if (_isLoaded)
            GL.DeleteTexture(ID);
    }
    ImageResult texture;
    private void gpuTexture() {
        ID = GL.GenTexture();
        texture = ImageResult.FromStream(
            File.OpenRead("Resources\\Default\\Texture.png"),
            ColorComponents.RedGreenBlueAlpha);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, ID);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);

    StbImage.stbi_set_flip_vertically_on_load(1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texture.Width, texture.Height,
            0, PixelFormat.Rgba, PixelType.UnsignedByte, texture.Data);
        UnBind();
    }
}