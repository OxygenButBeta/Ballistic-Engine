using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// A single RGBA16F color texture + FBO for post-process pass outputs (SSR, TAA history).
// Ensure() lazily (re)allocates on size change and reports whether contents were lost.
public sealed class GLRenderTexture {
    int fbo;
    public int Texture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    // Returns false when the texture was (re)created and previous contents are gone.
    public bool Ensure(int width, int height) {
        if (fbo == 0)
            fbo = GL.GenFramebuffer();
        if (Texture != 0 && width == Width && height == Height)
            return true;

        if (Texture != 0)
            GL.DeleteTexture(Texture);

        Width = width;
        Height = height;
        Texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, Texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0,
            PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, Texture, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return false;
    }

    // Binds as render target and sets the viewport.
    public void BindAsTarget() {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, Width, Height);
    }

    public void Dispose() {
        if (Texture != 0)
            GL.DeleteTexture(Texture);
        if (fbo != 0)
            GL.DeleteFramebuffer(fbo);
        Texture = 0;
        fbo = 0;
        Width = 0;
        Height = 0;
    }
}
