using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Screen-space reflections: ray-march the depth buffer in view space so smooth surfaces
// reflect the actual scene; the hit replaces the sky-IBL reflection baked into the color.
public sealed class GLSSRPass {
    readonly StandardShader marchShader;
    readonly StandardShader combineShader;

    public GLSSRPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        marchShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSR_Frag.glsl"));
        combineShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSR_Combine.glsl"));
    }

    // Returns a texture containing scene color with reflections applied (or the input
    // color texture unchanged when prerequisites are missing).
    public int Render(int targetIndex, int colorTexture, int depthTexture, int normalTexture, int width, int height,
        ref Matrix4 view, ref Matrix4 projection, PostProcessSettings fx) {
        if (depthTexture <= 0 || normalTexture <= 0)
            return colorTexture;

        // Frame-transient targets from the shared pool (released wholesale at frame end).
        // The march runs HALF-RES (32 steps x 5 refines per pixel is the most expensive
        // screen pass); the combine upsamples depth-aware, and TAA absorbs the difference.
        var halfW = Math.Max(1, width / 2);
        var halfH = Math.Max(1, height / 2);
        GLRenderTexture reflectionTarget = GLRenderTexturePool.Shared.Acquire(halfW, halfH);
        GLRenderTexture combinedTarget = GLRenderTexturePool.Shared.Acquire(width, height);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        // 1. March reflections.
        reflectionTarget.BindAsTarget();
        marchShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, colorTexture);
        marchShader.SetInt("colorTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        marchShader.SetInt("depthTexture", 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, normalTexture);
        marchShader.SetInt("normalTexture", 2);

        Matrix4 invProjection = Matrix4.Invert(projection);
        marchShader.SetMatrix4("Projection", ref projection);
        marchShader.SetMatrix4("InvProjection", ref invProjection);
        marchShader.SetMatrix4("ViewMatrix", ref view);
        marchShader.SetFloat("Intensity", fx.SsrIntensity);
        GLBufferUtilities.DrawFullscreenQuad();

        // 2. Composite over the scene color (depth-aware upsample of the half-res reflections).
        combinedTarget.BindAsTarget();
        combineShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, colorTexture);
        combineShader.SetInt("sceneTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, reflectionTarget.Texture);
        combineShader.SetInt("ssrTexture", 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        combineShader.SetInt("depthTexture", 2);
        combineShader.SetMatrix4("InvProjection", ref invProjection);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return combinedTarget.Texture;
    }
}
