using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Physical depth of field: a thin-lens CoC from scene depth drives a half-res bokeh gather,
// then a full-res cross-fade with the sharp image. Runs AFTER TAA (so it blurs the resolved,
// antialiased frame) and BEFORE bloom (so out-of-focus highlights bloom as bokeh). Fully
// deterministic — no temporal state — so it A/B-diffs cleanly under the paused screenshot
// harness. All scratch comes from the shared transient pool (nothing pass-owned to leak).
public sealed class GLDepthOfFieldPass {
    readonly StandardShader cocShader;
    readonly StandardShader bokehShader;
    readonly StandardShader combineShader;

    public GLDepthOfFieldPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        cocShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("DoF_Coc.glsl"));
        bokehShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("DoF_Bokeh.glsl"));
        combineShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("DoF_Combine.glsl"));
    }

    // Returns scene color with DoF applied, or the input unchanged when prerequisites are
    // missing or the effect is a no-op (aperture wide / DoF disabled is handled by the caller).
    public int Render(int colorTexture, int depthTexture, int width, int height,
        ref Matrix4 projection, PostProcessSettings fx) {
        if (depthTexture <= 0)
            return colorTexture;

        // ceil (not floor): full half-res coverage of an ODD full-res height (no clamped-edge upsample
        // flash at the bottom row under motion). Byte-identical for even dimensions.
        int halfW = System.Math.Max(1, (width + 1) / 2);
        int halfH = System.Math.Max(1, (height + 1) / 2);
        GLRenderTexture cocColor = GLRenderTexturePool.Shared.Acquire(halfW, halfH);
        GLRenderTexture bokeh = GLRenderTexturePool.Shared.Acquire(halfW, halfH);
        GLRenderTexture combined = GLRenderTexturePool.Shared.Acquire(width, height);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        Matrix4 invProjection = Matrix4.Invert(projection);

        // 1. CoC + downsample to half-res (rgb = color, a = signed CoC fraction).
        cocColor.BindAsTarget();
        cocShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, colorTexture);
        cocShader.SetInt("colorTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        cocShader.SetInt("depthTexture", 1);
        cocShader.SetMatrix4("InvProjection", ref invProjection);
        cocShader.SetFloat("FocusDistance", fx.DofFocusDistance);
        cocShader.SetFloat("FocalLength", fx.DofFocalLength);
        cocShader.SetFloat("Aperture", fx.DofAperture);
        cocShader.SetFloat("MaxCoc", fx.DofMaxCoc);
        GLBufferUtilities.DrawFullscreenQuad();

        // 2. Bokeh gather at half-res.
        bokeh.BindAsTarget();
        bokehShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, cocColor.Texture);
        bokehShader.SetInt("cocColorTexture", 0);
        bokehShader.SetFloat2("TexelSize", new Vector2(1f / halfW, 1f / halfH));
        bokehShader.SetFloat("MaxCoc", fx.DofMaxCoc);
        GLBufferUtilities.DrawFullscreenQuad();

        // 3. Full-res composite: cross-fade sharp scene with the upsampled bokeh.
        combined.BindAsTarget();
        combineShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, colorTexture);
        combineShader.SetInt("sceneTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, bokeh.Texture);
        combineShader.SetInt("dofTexture", 1);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return combined.Texture;
    }
}
