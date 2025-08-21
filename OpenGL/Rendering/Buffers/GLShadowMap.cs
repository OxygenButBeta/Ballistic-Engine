using OpenTK.Graphics.OpenGL4;

public class GLShadowMap : IFrameBuffer {
    public readonly int FrameBufferId;
    public readonly int DepthTextureId;
    public readonly int Width, Height;

    public GLShadowMap(int width, int height) {
        Width = width;
        Height = height;

        FrameBufferId = GL.GenFramebuffer();
        DepthTextureId = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2D, DepthTextureId);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent,
            width, height, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);

        float[] borderColor = { 1.0f, 1.0f, 1.0f, 1.0f };
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, borderColor);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, FrameBufferId);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, DepthTextureId, 0);

        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            Console.WriteLine("Shadow framebuffer not complete: " + status);
    }

    public void Bind() {
        GL.Viewport(0, 0, Width, Height);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, FrameBufferId);
        GL.Clear(ClearBufferMask.DepthBufferBit);
    }

    public int LenX => Width;
    public int LenY => Height;

    public void Activate() {
        Bind();
    }

    public void Unbind() {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void BindTexture() {
        GL.ActiveTexture(TextureUnit.Texture31);
        GL.BindTexture(TextureTarget.Texture2D, DepthTextureId);
    }
}