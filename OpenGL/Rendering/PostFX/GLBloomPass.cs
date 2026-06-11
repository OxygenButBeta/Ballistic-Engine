using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Progressive downsample/upsample bloom (Jimenez, SIGGRAPH 2014 "Next Generation
// Post Processing in Call of Duty: Advanced Warfare"). One RGBA16F texture per level
// (half res, quarter, ...); the upsample pass tent-filters back up with additive
// blending. Render returns the half-res result texture for the composite pass.
public sealed class GLBloomPass {
    const int MaxLevels = 6;

    readonly StandardShader downsampleShader;
    readonly StandardShader upsampleShader;

    readonly int[] levelTextures = new int[MaxLevels];
    readonly int[] levelWidths = new int[MaxLevels];
    readonly int[] levelHeights = new int[MaxLevels];
    int levelCount;
    int fbo;
    int allocatedWidth, allocatedHeight;

    public GLBloomPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        downsampleShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("Bloom_Down.glsl"));
        upsampleShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("Bloom_Up.glsl"));
    }

    void EnsureChain(int sourceWidth, int sourceHeight) {
        if (fbo == 0)
            fbo = GL.GenFramebuffer();
        if (levelTextures[0] != 0 && sourceWidth == allocatedWidth && sourceHeight == allocatedHeight)
            return;

        allocatedWidth = sourceWidth;
        allocatedHeight = sourceHeight;
        ReleaseTextures();

        var w = Math.Max(1, sourceWidth / 2);
        var h = Math.Max(1, sourceHeight / 2);
        levelCount = 0;
        for (var i = 0; i < MaxLevels && w >= 8 && h >= 8; i++) {
            levelTextures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, levelTextures[i]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, w, h, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            levelWidths[i] = w;
            levelHeights[i] = h;
            levelCount++;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
    }

    void ReleaseTextures() {
        for (var i = 0; i < MaxLevels; i++) {
            if (levelTextures[i] != 0)
                GL.DeleteTexture(levelTextures[i]);
            levelTextures[i] = 0;
        }
    }

    // Returns the bloom texture (half resolution), or 0 when the target is too small.
    public int Render(int sourceHdrTexture, int sourceWidth, int sourceHeight, PostProcessSettings fx) {
        EnsureChain(sourceWidth, sourceHeight);
        if (levelCount == 0)
            return 0;

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.ActiveTexture(TextureUnit.Texture0);

        // Downsample chain: HDR scene -> level 0 (thresholded) -> level 1 -> ...
        downsampleShader.Activate();
        downsampleShader.SetInt("sourceTexture", 0);
        for (var i = 0; i < levelCount; i++) {
            int srcW, srcH;
            if (i == 0) {
                GL.BindTexture(TextureTarget.Texture2D, sourceHdrTexture);
                srcW = sourceWidth;
                srcH = sourceHeight;
            }
            else {
                GL.BindTexture(TextureTarget.Texture2D, levelTextures[i - 1]);
                srcW = levelWidths[i - 1];
                srcH = levelHeights[i - 1];
            }

            downsampleShader.SetBool("applyThreshold", i == 0);
            downsampleShader.SetFloat("threshold", fx.BloomThreshold);
            downsampleShader.SetFloat2("sourceTexelSize", new Vector2(1f / srcW, 1f / srcH));

            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, levelTextures[i], 0);
            GL.Viewport(0, 0, levelWidths[i], levelHeights[i]);
            GLBufferUtilities.DrawFullscreenQuad();
        }

        // Upsample: tent-filter each smaller level additively onto the next larger one.
        upsampleShader.Activate();
        upsampleShader.SetInt("sourceTexture", 0);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        for (var i = levelCount - 2; i >= 0; i--) {
            GL.BindTexture(TextureTarget.Texture2D, levelTextures[i + 1]);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, levelTextures[i], 0);
            GL.Viewport(0, 0, levelWidths[i], levelHeights[i]);
            GLBufferUtilities.DrawFullscreenQuad();
        }

        GL.Disable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return levelTextures[0];
    }
}
