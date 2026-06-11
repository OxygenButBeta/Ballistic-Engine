using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// Multisampled HDR render target. The scene renders into this when MSAA is on,
// then resolves (color + depth) into a regular GLFrameBuffer for post-processing.
public sealed class GLMultisampleFrameBuffer {
    int fboId, colorRbo, depthRbo;
    public int Samples { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public GLMultisampleFrameBuffer(int width, int height, int samples) {
        Create(width, height, samples);
    }

    void Create(int width, int height, int samples) {
        Width = width;
        Height = height;
        Samples = samples;

        fboId = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fboId);

        colorRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRbo);
        GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, RenderbufferStorage.Rgba16f,
            width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, colorRbo);

        depthRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRbo);
        GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples,
            RenderbufferStorage.DepthComponent24, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depthRbo);

        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            Console.WriteLine("MSAA framebuffer is not complete!");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Resize(int width, int height, int samples) {
        if (width == Width && height == Height && samples == Samples)
            return;
        Dispose();
        Create(width, height, samples);
    }

    public void Activate() => GL.BindFramebuffer(FramebufferTarget.Framebuffer, fboId);

    // Resolve color and depth into a single-sample framebuffer. The destination must have a
    // depth texture/renderbuffer of matching format and the same dimensions.
    public void BlitTo(GLFrameBuffer destination) {
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fboId);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destination.FrameBufferId);
        GL.BlitFramebuffer(0, 0, Width, Height, 0, 0, destination.LenX, destination.LenY,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BlitFramebuffer(0, 0, Width, Height, 0, 0, destination.LenX, destination.LenY,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose() {
        GL.DeleteRenderbuffer(colorRbo);
        GL.DeleteRenderbuffer(depthRbo);
        GL.DeleteFramebuffer(fboId);
    }
}
