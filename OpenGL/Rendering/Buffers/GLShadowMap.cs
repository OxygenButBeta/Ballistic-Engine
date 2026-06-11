using OpenTK.Graphics.OpenGL4;

// Depth texture ARRAY for cascaded (and future layered) shadow maps: one layer per cascade,
// sampled in shaders as sampler2DArrayShadow (hardware-PCF compare per tap).
public class GLShadowMap : IFrameBuffer {
    public readonly int FrameBufferId;
    public readonly int DepthTextureId;
    public readonly int Width, Height, Layers;

    public GLShadowMap(int width, int height, int layers = 1) {
        Width = width;
        Height = height;
        Layers = layers;

        FrameBufferId = GL.GenFramebuffer();
        DepthTextureId = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2DArray, DepthTextureId);
        GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.DepthComponent24,
            width, height, layers, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);

        // Linear + compare mode: the shader samples this as sampler2DArrayShadow, so every
        // tap is a hardware-filtered 2x2 PCF comparison.
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode,
            (int)TextureCompareMode.CompareRToTexture);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToBorder);

        float[] borderColor = { 1.0f, 1.0f, 1.0f, 1.0f };
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBorderColor, borderColor);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, FrameBufferId);
        GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            DepthTextureId, 0, 0);

        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            Console.WriteLine("Shadow framebuffer not complete: " + status);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Binds the FBO targeting one cascade layer, sets the viewport and clears its depth.
    public void Bind(int layer) {
        GL.Viewport(0, 0, Width, Height);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, FrameBufferId);
        GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            DepthTextureId, 0, layer);
        GL.Clear(ClearBufferMask.DepthBufferBit);
    }

    public void Bind() => Bind(0);

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
        GL.BindTexture(TextureTarget.Texture2DArray, DepthTextureId);
    }

    // Releases the GL objects (used when the renderer recreates the map at a new resolution).
    public void Dispose() {
        GL.DeleteFramebuffer(FrameBufferId);
        GL.DeleteTexture(DepthTextureId);
    }
}
