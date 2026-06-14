using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Screen-space reflections: ray-march the depth buffer in view space so smooth surfaces
// reflect the actual scene; the hit replaces the sky-IBL reflection baked into the color.
//
// The raw half-res march hit-point wobbles frame-to-frame (TAA jitters the depth buffer the
// march reads), so the reflection shimmers. A TEMPORAL ACCUMULATION pass (reproject + disocclusion
// reject + colour-clamped EMA, the SSGI pattern) sits between the march and the composite to
// resolve that flicker into a stable reflection. History is pass-owned (ping-pong); a resize
// drops it.
public sealed class GLSSRPass {
    readonly StandardShader marchShader;
    readonly StandardShader temporalShader;
    readonly StandardShader combineShader;

    // Ping-pong accumulated reflection + its view-depth (for next frame's disocclusion test).
    readonly GLRenderTexture[] historySSR = { new(), new() };
    readonly GLRenderTexture[] historyDepth = { new(), new() };
    int historyWrite;
    bool hasHistory;
    Matrix4 prevViewProjection;
    int temporalFbo;

    // Accumulation window. Reflections are sharper than diffuse GI, so a shorter window keeps them
    // responsive while still killing the per-frame march jitter; the colour clamp prevents ghosting.
    const float MaxHistory = 8f;

    public GLSSRPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        marchShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSR_Frag.glsl"));
        temporalShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSR_Temporal.glsl"));
        combineShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSR_Combine.glsl"));
    }

    // Returns a texture containing scene color with reflections applied (or the input
    // color texture unchanged when prerequisites are missing). projectionNoJitter is the
    // UN-jittered projection used for the temporal reprojection (the accumulated image is
    // jitter-free); projection is this frame's (jittered) projection the march reconstructs from.
    public int Render(int targetIndex, int colorTexture, int depthTexture, int normalTexture, int width, int height,
        ref Matrix4 view, ref Matrix4 projection, ref Matrix4 projectionNoJitter, PostProcessSettings fx) {
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

        Matrix4 invProjection = Matrix4.Invert(projection);

        // 1. March reflections (raw, this frame, half-res).
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

        marchShader.SetMatrix4("Projection", ref projection);
        marchShader.SetMatrix4("InvProjection", ref invProjection);
        marchShader.SetMatrix4("ViewMatrix", ref view);
        marchShader.SetFloat("Intensity", fx.SsrIntensity);
        GLBufferUtilities.DrawFullscreenQuad();

        // 2. Temporal accumulation: reproject last frame's reflection, disocclusion-reject, EMA.
        // Uses the UN-jittered projection for reprojection (the accumulated image is jitter-free).
        int ssrForComposite = reflectionTarget.Texture;
        {
            Matrix4 invView = Matrix4.Invert(view);
            Matrix4 invProjNoJitter = Matrix4.Invert(projectionNoJitter);
            Matrix4 viewProjNoJitter = view * projectionNoJitter;

            int readSlot = historyWrite;
            int writeSlot = 1 - readSlot;
            GLRenderTexture ssrRead = historySSR[readSlot];
            GLRenderTexture ssrWriteTex = historySSR[writeSlot];
            GLRenderTexture depthReadTex = historyDepth[readSlot];
            GLRenderTexture depthWriteTex = historyDepth[writeSlot];

            bool sizeKept = ssrWriteTex.Ensure(halfW, halfH);
            ssrRead.Ensure(halfW, halfH);
            depthWriteTex.Ensure(halfW, halfH);
            depthReadTex.Ensure(halfW, halfH);
            if (!sizeKept)
                hasHistory = false;

            if (temporalFbo == 0)
                temporalFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, temporalFbo);
            GL.Viewport(0, 0, halfW, halfH);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, ssrWriteTex.Texture, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
                TextureTarget.Texture2D, depthWriteTex.Texture, 0);
            GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });

            temporalShader.Activate();
            BindTex(temporalShader, 0, reflectionTarget.Texture, "currentSSR");
            BindTex(temporalShader, 1, ssrRead.Texture, "historySSR");
            BindTex(temporalShader, 2, depthReadTex.Texture, "historyDepth");
            BindTex(temporalShader, 3, depthTexture, "depthTexture");
            temporalShader.SetMatrix4("InvProjection", ref invProjNoJitter);
            temporalShader.SetMatrix4("InvViewMatrix", ref invView);
            temporalShader.SetMatrix4("PrevViewProjection", ref prevViewProjection);
            temporalShader.SetBool("HasHistory", hasHistory);
            temporalShader.SetFloat("MaxHistory", MaxHistory);
            GLBufferUtilities.DrawFullscreenQuad();

            // Restore single-target draw state before the composite.
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
                TextureTarget.Texture2D, 0, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);

            ssrForComposite = ssrWriteTex.Texture;
            historyWrite = writeSlot;
            hasHistory = true;
            prevViewProjection = viewProjNoJitter;
        }

        // 3. Composite over the scene color (depth-aware upsample of the half-res reflections).
        combinedTarget.BindAsTarget();
        combineShader.Activate();
        BindTex(combineShader, 0, colorTexture, "sceneTexture");
        BindTex(combineShader, 1, ssrForComposite, "ssrTexture");
        BindTex(combineShader, 2, depthTexture, "depthTexture");
        combineShader.SetMatrix4("InvProjection", ref invProjection);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return combinedTarget.Texture;
    }

    static void BindTex(StandardShader shader, int unit, int texture, string name) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        shader.SetInt(name, unit);
    }
}
