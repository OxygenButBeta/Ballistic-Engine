using System.Diagnostics.CodeAnalysis;
using BallisticEngine;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

[SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")]
public static class GLBufferUtilities {

    static readonly GPUBuffer<Vector2> quadBuffer;
    static readonly GPUBuffer<Vector2> quadUVBuffer;
    static readonly RenderContext renderContext;
    static readonly StandardShader quadShader;

    static GLBufferUtilities() {
        Vector2[] vertices = {
            new(-1f, -1f),
            new(1f, -1f),
            new(-1f, 1f),
            new(-1f, 1f),
            new(1f, -1f),
            new(1f, 1f)
        };
        Vector2[] uvs = {
            new(0f, 0f),
            new(1f, 0f),
            new(0f, 1f),
            new(0f, 1f),
            new(1f, 0f),
            new(1f, 1f)
        };

        renderContext = GraphicAPI.CreateRenderContext();
        quadBuffer = GraphicAPI.CreateVertexBuffer2(renderContext);
        quadUVBuffer = GraphicAPI.CreateUVBuffer(renderContext);

        quadBuffer.Create();
        quadUVBuffer.Create();

        quadBuffer.SetBufferData(vertices, BufferUsageHint.StaticDraw);
        quadUVBuffer.SetBufferData(uvs, BufferUsageHint.StaticDraw);

        renderContext.Deactivate();
        
        quadShader = GraphicAPI.CreateStandardShader(Resources.ReadResourceText("Shaders/FSQ_Vert.glsl"),
            Resources.ReadResourceText("Shaders/FSQ_Frag.glsl"));
    }


    public static void DrawBufferToScreen(this IFrameBuffer buffer) {
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Viewport(0, 0, buffer.LenX, buffer.LenY);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        
        buffer.Unbind();
        quadShader.Activate();
        quadShader.SetInt("hdrTexture", 31);
        renderContext.Activate();
        buffer.BindTexture();
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }
}

public interface IFrameBuffer {
    int LenX { get; }
    int LenY { get; }
    void Activate();
    void Unbind();
    void BindTexture();
}
public class GLFrameBuffer : IFrameBuffer {
    readonly int frameBufferId;
    public int LenX { get; private set; }
    public int LenY { get; private set; }
    public int colorBuffer, depthBufferId = -1;
    public GLFrameBuffer(int width, int height) {
        frameBufferId = GL.GenFramebuffer();
        CreateBuffers(width, height);
    }


    void CreateBuffers(int width, int height) {
        LenY = height;
        LenX = width;
        Activate();
        // Color Buffer
        colorBuffer = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, colorBuffer);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba,
            PixelType.Float, IntPtr.Zero);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, colorBuffer, 0);

        // Depth Buffer
        depthBufferId = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthBufferId);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depthBufferId);

        // Buffer Check
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            Console.WriteLine("HDR Framebuffer is not complete!");

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Resize(int width, int height) {
        ClearBuffers();
        CreateBuffers(width, height);
        Console.WriteLine("Framebuffer resized to {0}x{1}", width, height);
    }

    void ClearBuffers() {
        if (colorBuffer != 0) {
            GL.DeleteTexture(colorBuffer);
            colorBuffer = 0;
        }

        if (depthBufferId == -1)
            return;

        GL.DeleteRenderbuffer(depthBufferId);
        depthBufferId = -1;
    }

    public void BindTexture() {
        GL.ActiveTexture(TextureUnit.Texture31);
        GL.BindTexture(TextureTarget.Texture2D, colorBuffer);
    }

    public void Activate() {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, frameBufferId);
    }

    public void Unbind() {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }
}