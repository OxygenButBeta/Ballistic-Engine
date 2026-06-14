using System.Diagnostics.CodeAnalysis;
using BallisticEngine;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

[SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")]
public static class GLBufferUtilities {

    static readonly GPUBuffer<Vector2> quadBuffer;
    static readonly GPUBuffer<Vector2> quadUVBuffer;
    static readonly RenderContext renderContext;

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

        quadBuffer.SetBufferData(vertices, BufferUsage.StaticDraw);
        quadUVBuffer.SetBufferData(uvs, BufferUsage.StaticDraw);

        renderContext.Deactivate();
    }

    // Shared fullscreen-quad VAO for every post-process pass. The caller binds its
    // own shader and textures first.
    public static void DrawFullscreenQuad() {
        renderContext.Activate();
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
    readonly bool depthAsTexture;
    readonly bool withNormalAttachment;
    public int LenX { get; private set; }
    public int LenY { get; private set; }
    public int colorBuffer, depthBufferId = -1;

    // Valid when constructed with depthAsTexture: lets post passes (SSAO) sample scene depth.
    public int DepthTextureId { get; private set; } = -1;

    // Valid when constructed with withNormalAttachment: world normal (xyz) + roughness (a),
    // written by the PBR shader as a second render target for SSR/TAA.
    public int NormalTextureId { get; private set; } = -1;

    // Valid when constructed with withNormalAttachment: linear DIFFUSE ALBEDO (rgb), written by the
    // PBR shader as a THIRD render target. The deferred GI passes (SDF-GI / SSGI composite) multiply
    // the gathered irradiance by THIS per-pixel albedo so the bounce respects each surface's true
    // reflectance (a dark/low-albedo surface bounces little) instead of a flat constant — fixes the
    // enclosed bright-bounce over-saturation. RGBA8 is plenty for an albedo (0..1, sRGB-ish range).
    public int AlbedoTextureId { get; private set; } = -1;

    public int FrameBufferId => frameBufferId;

    public GLFrameBuffer(int width, int height, bool depthAsTexture = false, bool withNormalAttachment = false) {
        this.depthAsTexture = depthAsTexture;
        this.withNormalAttachment = withNormalAttachment;
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
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, colorBuffer, 0);

        if (withNormalAttachment) {
            NormalTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, NormalTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
                TextureTarget.Texture2D, NormalTextureId, 0);

            // Third target: linear diffuse albedo (RGB) for the deferred GI receiver-reflectance read.
            AlbedoTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, AlbedoTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2,
                TextureTarget.Texture2D, AlbedoTextureId, 0);

            GL.DrawBuffers(3, new[] {
                DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1,
                DrawBuffersEnum.ColorAttachment2,
            });
        }

        // Depth Buffer
        if (depthAsTexture) {
            DepthTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, DepthTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, width, height, 0,
                PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, DepthTextureId, 0);
        }
        else {
            depthBufferId = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthBufferId);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width,
                height);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depthBufferId);
        }

        // Buffer Check
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            Console.WriteLine("HDR Framebuffer is not complete!");

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Re-specifies the EXISTING texture objects at the new size instead of delete+recreate.
    // Texture IDs staying stable matters: the editor's UI pass records the color texture ID
    // BEFORE the scene renders (frame order is UI -> render -> present), so deleting the
    // texture mid-frame made every panel draw a dead handle during a resize drag (garbage /
    // black flicker until release). In-place realloc keeps every captured ID valid.
    public void Resize(int width, int height) {
        if (width == LenX && height == LenY)
            return;
        LenX = width;
        LenY = height;

        GL.BindTexture(TextureTarget.Texture2D, colorBuffer);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0,
            PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);

        if (NormalTextureId != -1) {
            GL.BindTexture(TextureTarget.Texture2D, NormalTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        }

        if (AlbedoTextureId != -1) {
            GL.BindTexture(TextureTarget.Texture2D, AlbedoTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        }

        if (DepthTextureId != -1) {
            GL.BindTexture(TextureTarget.Texture2D, DepthTextureId);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, width, height, 0,
                PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        }

        if (depthBufferId != -1) {
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthBufferId);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24,
                width, height);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    void ClearBuffers() {
        if (colorBuffer != 0) {
            GL.DeleteTexture(colorBuffer);
            colorBuffer = 0;
        }

        if (NormalTextureId != -1) {
            GL.DeleteTexture(NormalTextureId);
            NormalTextureId = -1;
        }

        if (AlbedoTextureId != -1) {
            GL.DeleteTexture(AlbedoTextureId);
            AlbedoTextureId = -1;
        }

        if (DepthTextureId != -1) {
            GL.DeleteTexture(DepthTextureId);
            DepthTextureId = -1;
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
