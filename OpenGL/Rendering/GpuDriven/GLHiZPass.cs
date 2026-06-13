using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GpuDriven;

// Builds a Hi-Z (hierarchical depth) pyramid from the camera depth buffer: a full mip chain of an
// R32F texture where each coarser mip holds the MAX (farthest) depth of its 2x2 footprint. The
// GPU-driven cull samples it to reject submeshes whose entire screen-space AABB is behind a closer
// occluder. MAX is the conservative reduction — the pyramid can only ever over-state how far the
// nearest occluder is, so the cull can never falsely claim something is occluded.
//
// The pyramid is built from the PREVIOUS frame's depth (the cull runs before this frame's depth
// exists). For a static/slow camera that's exact; on fast motion the renderer disables Hi-Z for a
// frame (the only way a stale pyramid could false-cull is geometry that became visible this frame,
// and the camera-delta gate covers it).
public sealed class GLHiZPass : IDisposable {
    public int PyramidTexture { get; private set; }
    public int MipCount { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    readonly StandardShader copyShader;   // linearize/copy depth -> mip 0
    readonly StandardShader downShader;   // MAX downsample mip N -> N+1
    int fbo;
    int allocW, allocH;

    public GLHiZPass() {
        string vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        // mip 0 is a straight copy of the depth's R channel into the R32F pyramid.
        copyShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("HiZ_Copy.glsl"));
        downShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("HiZ_Down.glsl"));
    }

    void Ensure(int width, int height) {
        if (PyramidTexture != 0 && width == allocW && height == allocH)
            return;
        Dispose();
        allocW = width;
        allocH = height;
        Width = width;
        Height = height;
        MipCount = 1 + (int)System.MathF.Floor(System.MathF.Log2(System.Math.Max(width, height)));

        PyramidTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, PyramidTexture);
        GL.TexStorage2D(TextureTarget2d.Texture2D, MipCount, SizedInternalFormat.R32f, width, height);
        // NEAREST + explicit LOD sampling in the cull; clamp so edge AABBs don't wrap.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.NearestMipmapNearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, MipCount - 1);

        if (fbo == 0)
            fbo = GL.GenFramebuffer();
    }

    // Builds the full pyramid from `depthTexture` (the camera depth attachment). Restores no GL
    // state the renderer relies on beyond unbinding the FBO; callers re-bind their target after.
    public void Build(int depthTexture, int width, int height) {
        if (depthTexture <= 0)
            return;
        Ensure(width, height);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        // ---- mip 0: copy depth's R channel into the pyramid ----
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, PyramidTexture, 0);
        GL.Viewport(0, 0, width, height);
        copyShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        copyShader.SetInt("SourceDepth", 0);
        GLBufferUtilities.DrawFullscreenQuad();

        // ---- mips 1..N: MAX downsample, reading the level just written ----
        int mipW = width, mipH = height;
        for (int mip = 1; mip < MipCount; mip++) {
            int srcW = mipW, srcH = mipH;
            mipW = System.Math.Max(1, mipW / 2);
            mipH = System.Math.Max(1, mipH / 2);

            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, PyramidTexture, mip);
            GL.Viewport(0, 0, mipW, mipH);
            downShader.Activate();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, PyramidTexture);
            // Read exactly the previous level (base==max==mip-1) to avoid feedback with the write level.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, mip - 1);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mip - 1);
            downShader.SetInt("SourceDepth", 0);
            downShader.SetFloat2("SourceSize", new Vector2(srcW, srcH));
            GLBufferUtilities.DrawFullscreenQuad();
        }

        // Restore the full mip range for sampling in the cull.
        GL.BindTexture(TextureTarget.Texture2D, PyramidTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, MipCount - 1);

        // Detach the pyramid from our FBO and leave clean state: a stray color attachment or a
        // texture left bound on unit 0 corrupts the passes that run after (sky/SSGI/SSR all sample
        // their own textures on unit 0 and assume nothing of ours lingers). This was the bug that
        // made merely BUILDING the pyramid change the image even when the cull dropped nothing.
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, 0, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Enable(EnableCap.DepthTest);   // restore the defaults the build disabled
        GL.Enable(EnableCap.CullFace);
    }

    // Debug: read back min/max of mip 0 and the coarsest mip's single value (blocks — debug only).
    public (float Mip0Min, float Mip0Max, float CoarsestMax) DebugStats() {
        if (PyramidTexture == 0)
            return (0, 0, 0);
        GL.BindTexture(TextureTarget.Texture2D, PyramidTexture);
        var mip0 = new float[Width * Height];
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Red, PixelType.Float, mip0);
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (float v in mip0) { if (v < mn) mn = v; if (v > mx) mx = v; }
        var coarse = new float[1];
        GL.GetTexImage(TextureTarget.Texture2D, MipCount - 1, PixelFormat.Red, PixelType.Float, coarse);
        return (mn, mx, coarse[0]);
    }

    public void Dispose() {
        if (PyramidTexture != 0) {
            GL.DeleteTexture(PyramidTexture);
            PyramidTexture = 0;
        }
    }
}
