using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Production-style screen-space global illumination: a SSR-style hemisphere gather followed
// by a temporal-accumulation + edge-aware spatial denoise, then an artistic composite.
//
// Pipeline per frame (all half-res except the final combine):
//   1. March    - noisy one-bounce gather (SSGI_Frag), reads last frame's GI for multi-bounce
//   2. Temporal - reproject + accumulate history (SSGI_Temporal); the main noise win
//   3. Denoise  - edge-aware a-trous wavelet (SSGI_Denoise); cleans within-frame noise
//   4. Combine  - intensity/tint/saturation/occlusion, add over the full-res scene
//
// History (accumulated GI) persists per render target across frames and is reset on resize.
// Like SSR it needs the single-sample normal/depth attachments, so it only runs with MSAA off.
public sealed class GLSSGIPass {
    readonly StandardShader marchShader;
    readonly StandardShader temporalShader;
    readonly StandardShader denoiseShader;
    readonly StandardShader combineShader;

    // Per render target (Scene/Game view): only the HISTORY persists across frames (temporal
    // accumulation); the march/denoise/combine scratch comes from the shared transient pool.
    readonly GLRenderTexture[,] historyTargets = {
        { new(), new() },   // target 0: history A / B
        { new(), new() },   // target 1: history A / B
    };

    // View-space linear depth at each history pixel, ping-ponged in lockstep with historyTargets.
    // The temporal pass writes it (MRT attachment 1) and reads last frame's to reject reprojected
    // history at disocclusions (silhouettes under camera motion) — the salt-and-pepper fix.
    readonly GLRenderTexture[,] historyDepthTargets = {
        { new(), new() },
        { new(), new() },
    };

    // A private FBO for the temporal MRT step (GI history + view-depth in one draw). The pooled
    // GLRenderTexture is single-attachment, so the two history textures are attached here per frame.
    int temporalFbo;

    readonly bool[] hasHistory = new bool[2];
    readonly int[] historyWrite = new int[2];   // which of the two history buffers to write
    readonly Matrix4[] prevViewProjection = new Matrix4[2];

    int frameIndex;

    public GLSSGIPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        marchShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSGI_Frag.glsl"));
        temporalShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSGI_Temporal.glsl"));
        denoiseShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSGI_Denoise.glsl"));
        combineShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSGI_Combine.glsl"));
    }

    // `projection` is the JITTERED matrix the depth/normals were rasterized with - the march
    // must invert exactly that (see the renderer's jitter note). `projectionNoJitter` drives
    // the TEMPORAL reprojection: history is an accumulated, effectively jitter-free image, so
    // reprojecting with jittered matrices made the history UV wobble with the jitter sequence
    // every frame and the accumulation could never converge (THE "SSGI is broken" shimmer).
    public int Render(int targetIndex, int colorTexture, int depthTexture, int normalTexture,
        int aoTexture, int width, int height, ref Matrix4 view, ref Matrix4 projection,
        ref Matrix4 projectionNoJitter,
        int envCubemap, ref Matrix4 skyRotation, float skyExposure, PostProcessSettings fx) {
        if (depthTexture <= 0 || normalTexture <= 0)
            return colorTexture;

        var halfW = Math.Max(1, width / 2);
        var halfH = Math.Max(1, height / 2);

        GLRenderTexture giTarget = GLRenderTexturePool.Shared.Acquire(halfW, halfH);
        GLRenderTexture denoiseTarget = GLRenderTexturePool.Shared.Acquire(halfW, halfH);
        GLRenderTexture combinedTarget = GLRenderTexturePool.Shared.Acquire(width, height);
        int readSlot = historyWrite[targetIndex];
        int writeSlot = 1 - readSlot;
        GLRenderTexture historyRead = historyTargets[targetIndex, readSlot];
        GLRenderTexture historyWriteTex = historyTargets[targetIndex, writeSlot];
        GLRenderTexture depthRead = historyDepthTargets[targetIndex, readSlot];
        GLRenderTexture depthWriteTex = historyDepthTargets[targetIndex, writeSlot];

        // A resize invalidates the accumulated history (reprojection would smear).
        bool sizeKept = historyWriteTex.Ensure(halfW, halfH);
        historyRead.Ensure(halfW, halfH);
        depthWriteTex.Ensure(halfW, halfH);
        depthRead.Ensure(halfW, halfH);
        if (!sizeKept)
            hasHistory[targetIndex] = false;

        // The march samples historyRead for multi-bounce BEFORE the temporal pass validates
        // it; a freshly-allocated texture holds undefined memory, so clear it once. The depth
        // history is cleared to a far value so the first frame's disocclusion test rejects cleanly.
        if (!hasHistory[targetIndex]) {
            historyRead.BindAsTarget();
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            depthRead.BindAsTarget();
            GL.ClearColor(-1e9f, 0f, 0f, 1f); // far view-Z (negative) so nothing matches it
            GL.Clear(ClearBufferMask.ColorBufferBit);
        }

        Matrix4 invProjection = Matrix4.Invert(projection);                   // jittered: march
        Matrix4 invProjectionNoJitter = Matrix4.Invert(projectionNoJitter);   // temporal
        Matrix4 invView = Matrix4.Invert(view);
        Matrix4 viewProjection = view * projectionNoJitter;                   // history matrix

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        // ---- 1. March (half-res noisy gather) ----
        giTarget.BindAsTarget();
        marchShader.Activate();
        BindTex(0, colorTexture, marchShader, "colorTexture");
        BindTex(1, depthTexture, marchShader, "depthTexture");
        BindTex(2, normalTexture, marchShader, "normalTexture");
        BindTex(3, historyRead.Texture, marchShader, "historyColor"); // last frame for multi-bounce
        // Sky fallback for missed rays: the prefiltered environment, pre-exposed. envCubemap of
        // 0 (no skybox/IBL) disables the fallback via SkyFallback = 0.
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.TextureCubeMap, envCubemap);
        marchShader.SetInt("EnvironmentMap", 4);
        marchShader.SetMatrix4("SkyRotation", ref skyRotation);
        marchShader.SetFloat("SkyExposure", skyExposure);
        marchShader.SetFloat("SkyFallback",
            envCubemap != 0 ? Math.Clamp(fx.SsgiSkyFallback, 0f, 1f) : 0f);
        marchShader.SetFloat("MaxEnvMip", GLEnvironmentMaps.PrefilterMipCount - 1); // roughest mip
        marchShader.SetMatrix4("Projection", ref projection);
        marchShader.SetMatrix4("InvProjection", ref invProjection);
        marchShader.SetMatrix4("ViewMatrix", ref view);
        marchShader.SetInt("FrameIndex", frameIndex++ & 1023);
        marchShader.SetInt("RayCount", Math.Clamp(fx.SsgiRayCount, 1, 16));
        marchShader.SetFloat("RayLength", fx.SsgiRayLength);
        marchShader.SetFloat("Falloff", fx.SsgiFalloff);
        marchShader.SetFloat("Thickness", fx.SsgiThickness);
        marchShader.SetFloat("MultiBounce", Math.Clamp(fx.SsgiMultiBounce, 0f, 1f));
        marchShader.SetFloat("BounceBoost", Math.Max(fx.SsgiBounceBoost, 0f));
        GLBufferUtilities.DrawFullscreenQuad();

        // ---- 2. Temporal accumulate (writes the new history GI + view-depth via MRT) ----
        if (temporalFbo == 0)
            temporalFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, temporalFbo);
        GL.Viewport(0, 0, halfW, halfH);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, historyWriteTex.Texture, 0);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, depthWriteTex.Texture, 0);
        GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });

        temporalShader.Activate();
        BindTex(0, giTarget.Texture, temporalShader, "currentGI");
        BindTex(1, historyRead.Texture, temporalShader, "historyGI");
        BindTex(2, depthTexture, temporalShader, "depthTexture");
        BindTex(3, normalTexture, temporalShader, "normalTexture");
        BindTex(4, depthRead.Texture, temporalShader, "historyDepth");
        temporalShader.SetMatrix4("InvProjection", ref invProjectionNoJitter);
        temporalShader.SetMatrix4("InvViewMatrix", ref invView);
        temporalShader.SetMatrix4("PrevViewProjection", ref prevViewProjection[targetIndex]);
        temporalShader.SetBool("HasHistory", hasHistory[targetIndex]);
        // Cinematic look implies "rock solid": let the dial extend the accumulation window so
        // a higher look is also smoother/less shimmery (the temporal pass is the main noise win).
        float look = Math.Clamp(fx.SsgiLook, 0f, 1f);
        temporalShader.SetFloat("MaxHistory", Math.Max(fx.SsgiMaxHistory, 1f) * (1f + look));
        GLBufferUtilities.DrawFullscreenQuad();

        // Restore single-target draw state for the subsequent denoise/combine passes.
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, 0, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);

        // ---- 3. Spatial denoise: a TWO-ITERATION a-trous wavelet cascade (SVGF-style) ----
        // One pass over a sparse 4-ray-at-half-res signal leaves low-frequency blotches - the
        // "weirdly noisy" structured grain. A second pass at 2x the tap spacing (the a-trous hole
        // doubling) cleans those without extra blur on edges, because each pass re-applies the
        // depth/normal/luma edge-stops. Ping-pong giTarget <-> denoiseTarget between iterations.
        float baseStep = Math.Max(fx.SsgiDenoise, 1f) * (1f + 0.5f * look);
        GLRenderTexture src = historyWriteTex;
        GLRenderTexture[] pingPong = { denoiseTarget, giTarget };
        for (var iter = 0; iter < 2; iter++) {
            GLRenderTexture dst = pingPong[iter & 1];
            dst.BindAsTarget();
            denoiseShader.Activate();
            BindTex(0, src.Texture, denoiseShader, "giTexture");
            BindTex(1, depthTexture, denoiseShader, "depthTexture");
            BindTex(2, normalTexture, denoiseShader, "normalTexture");
            denoiseShader.SetMatrix4("InvProjection", ref invProjection);
            denoiseShader.SetFloat("StepSize", baseStep * (1 << iter)); // 1x then 2x tap spacing
            denoiseShader.SetFloat("DepthSigma", 0.1f);
            denoiseShader.SetFloat("NormalSigma", 32f);
            GLBufferUtilities.DrawFullscreenQuad();
            src = dst;
        }
        GLRenderTexture denoisedFinal = src;

        // ---- 4. Combine over the full-res scene ----
        combinedTarget.BindAsTarget();
        combineShader.Activate();
        BindTex(0, colorTexture, combineShader, "sceneTexture");
        BindTex(1, denoisedFinal.Texture, combineShader, "ssgiTexture");
        BindTex(2, aoTexture, combineShader, "aoTexture");
        BindTex(3, normalTexture, combineShader, "normalTexture");
        combineShader.SetBool("ApplyAO", aoTexture != 0);
        combineShader.SetBool("DebugView", fx.SsgiDebugView);
        combineShader.SetFloat("Look", Math.Clamp(fx.SsgiLook, 0f, 1f)); // THE cinematic dial
        combineShader.SetFloat("Intensity", fx.SsgiIntensity);
        combineShader.SetFloat3("Tint", fx.SsgiTint);
        combineShader.SetFloat("Saturation", Math.Max(fx.SsgiSaturation, 0f));
        combineShader.SetFloat("OcclusionPower", Math.Max(fx.SsgiOcclusionPower, 0f));
        combineShader.SetFloat("AmbientFloor", Math.Max(fx.SsgiAmbientFloor, 0f));
        combineShader.SetFloat("EdgeFade", 1f); // edge confidence is already baked into the gather
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Advance temporal state: this frame's history becomes next frame's read, and the
        // matrix used to reproject becomes next frame's "previous".
        historyWrite[targetIndex] = writeSlot;
        hasHistory[targetIndex] = true;
        prevViewProjection[targetIndex] = viewProjection;

        return combinedTarget.Texture;
    }

    static void BindTex(int unit, int texture, StandardShader shader, string name) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        shader.SetInt(name, unit);
    }
}
