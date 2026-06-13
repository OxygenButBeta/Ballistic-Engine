using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Volumetric height fog + sun scattering (god-rays / light shafts). Raymarches a physical
// exponential height fog against the directional shadow map: in-scatters the atmosphere-
// attenuated sun plus the baked sky's average radiance (skylight), carries transmittance
// in alpha so the combine extinguishes the scene behind the fog, then temporally denoises
// and bilateral-upsamples the result.
//
// Pipeline per frame (march + temporal at half-res, combine at full-res):
//   1. March    - dithered raymarch, shadow-map visibility per step (Volumetric_Frag)
//   2. Temporal - reproject + accumulate last frame's fog rgba (Volumetric_Temporal)
//   3. Combine  - depth-aware upsample + scene*T + scatter composite (Volumetric_Combine)
//
// History persists per render target across frames and resets on resize. Like SSR/SSGI it
// reconstructs world pos from the single-sample depth attachment, so it only runs with MSAA
// off (i.e. when TAA is active) - which also lets TAA further stabilize the shafts.
public sealed class GLVolumetricFogPass {
    readonly StandardShader marchShader;
    readonly StandardShader temporalShader;
    readonly StandardShader combineShader;

    // Per render target (Scene/Game view): only the HISTORY persists across frames; the
    // march/combine scratch comes from the shared transient pool.
    readonly GLRenderTexture[,] historyTargets = {
        { new(), new() },   // target 0: history A / B
        { new(), new() },   // target 1: history A / B
    };

    readonly bool[] hasHistory = new bool[2];
    readonly int[] historyWrite = new int[2];   // which of the two history buffers to write
    readonly Matrix4[] prevViewProjection = new Matrix4[2];

    int frameIndex;

    public GLVolumetricFogPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        marchShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("Volumetric_Frag.glsl"));
        temporalShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("Volumetric_Temporal.glsl"));
        combineShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("Volumetric_Combine.glsl"));
    }

    static readonly string[] CascadeMatrixNames =
        { "CascadeMatrices[0]", "CascadeMatrices[1]", "CascadeMatrices[2]", "CascadeMatrices[3]" };

    // Returns the scene color with volumetric shafts composited in, or the input color
    // texture unchanged when prerequisites (depth / shadow map) are missing.
    //
    // uploadPunctual (optional): when the punctual-shaft dial is on, the renderer uses this to
    // push the SAME point/spot light arrays + punctual shadow map it feeds the lit shader onto
    // the march program — so a fog beam matches the surface lighting of the same light. It owns
    // the indexed-name arrays and the shadow texture bind; the pass stays light-agnostic.
    public int Render(int targetIndex, int colorTexture, int depthTexture, int shadowMapTexture,
        int width, int height, ref Matrix4 view, ref Matrix4 projection,
        Matrix4[] cascadeMatrices, Vector4 cascadeBias, int cascadeCount,
        Vector3 cameraPos, Vector3 sunDirection, Vector3 sunColor, Vector3 skyAmbient,
        float shadowDistance, PostProcessSettings fx, Action<StandardShader> uploadPunctual = null) {
        if (depthTexture <= 0 || shadowMapTexture <= 0)
            return colorTexture;

        var halfW = Math.Max(1, width / 2);
        var halfH = Math.Max(1, height / 2);

        GLRenderTexture scatterTarget = GLRenderTexturePool.Shared.Acquire(halfW, halfH);
        GLRenderTexture combinedTarget = GLRenderTexturePool.Shared.Acquire(width, height);
        int readSlot = historyWrite[targetIndex];
        int writeSlot = 1 - readSlot;
        GLRenderTexture historyRead = historyTargets[targetIndex, readSlot];
        GLRenderTexture historyWriteTex = historyTargets[targetIndex, writeSlot];

        // A resize invalidates accumulated history (reprojection would smear).
        bool sizeKept = historyWriteTex.Ensure(halfW, halfH);
        historyRead.Ensure(halfW, halfH);
        if (!sizeKept)
            hasHistory[targetIndex] = false;

        Matrix4 invProjection = Matrix4.Invert(projection);
        Matrix4 invView = Matrix4.Invert(view);
        Matrix4 viewProjection = view * projection;

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        // ---- 1. March (half-res, dithered) ----
        scatterTarget.BindAsTarget();
        marchShader.Activate();
        BindTex(0, depthTexture, marchShader, "depthTexture");
        BindShadow(1, shadowMapTexture, marchShader, "shadowMap");
        marchShader.SetMatrix4("InvProjection", ref invProjection);
        marchShader.SetMatrix4("InvViewMatrix", ref invView);
        var marchCascades = Math.Min(cascadeCount, CascadeMatrixNames.Length);
        for (var i = 0; i < marchCascades; i++)
            marchShader.SetMatrix4(CascadeMatrixNames[i], ref cascadeMatrices[i]);
        marchShader.SetFloat4("CascadeBias", cascadeBias);
        marchShader.SetInt("CascadeCount", marchCascades);
        marchShader.SetFloat3("SunDirectionWorld", sunDirection);
        marchShader.SetFloat3("SunColor", sunColor);
        marchShader.SetFloat3("SkyAmbient", skyAmbient);
        marchShader.SetFloat3("CameraPosWorld", cameraPos);
        marchShader.SetInt("StepCount", Math.Clamp(fx.VolumetricStepCount, 8, 256));
        marchShader.SetFloat("Anisotropy", Math.Clamp(fx.VolumetricAnisotropy, 0f, 0.95f));
        marchShader.SetFloat("SunGlow", Math.Max(fx.VolumetricSunGlow, 0f));
        marchShader.SetFloat("SunGlowSharpness", Math.Max(fx.VolumetricSunGlowSharpness, 1f));
        marchShader.SetInt("FrameIndex", frameIndex++ & 1023);

        // --- Medium: the physical fog when it's on, else the shafts' own thin medium ---
        // When VolumetricFog is enabled the march uses its physical medium unchanged (the fog
        // look is the accurate solution). When ONLY LightShafts is on, we still need a medium
        // to scatter the beams in, but we DON'T want the fog's hazy veil — so the medium uses
        // the shaft density with the physical sun/sky in-scatter ZEROED: only the additive
        // god-ray terms below light up, giving clean beams over a clear scene.
        bool fogOn = fx.VolumetricEnabled;
        if (fogOn) {
            marchShader.SetFloat("Density", Math.Max(fx.VolumetricDensity, 0f));
            marchShader.SetFloat("HeightFalloff", Math.Max(fx.VolumetricHeightFalloff, 0f));
            marchShader.SetFloat("BaseHeight", fx.VolumetricBaseHeight);
            marchShader.SetFloat("Scattering", Math.Max(fx.VolumetricScattering, 0f));
            marchShader.SetFloat("AmbientScatter", Math.Max(fx.VolumetricAmbientScatter, 0f));
        } else {
            marchShader.SetFloat("Density", Math.Max(fx.LightShaftsDensity, 0f));
            marchShader.SetFloat("HeightFalloff", 0f);   // uniform medium: beams reach up, not just ground haze
            marchShader.SetFloat("BaseHeight", 0f);
            marchShader.SetFloat("Scattering", 0f);      // no physical fog veil — shafts only
            marchShader.SetFloat("AmbientScatter", 0f);
        }

        // --- Light shafts / god-rays (additive; driven by the standalone LightShafts comp) ---
        bool shaftsOn = fx.LightShaftsEnabled;
        marchShader.SetFloat("SunShafts", shaftsOn ? Math.Max(fx.LightShaftsSun, 0f) : 0f);
        float punctualShafts = shaftsOn ? Math.Max(fx.LightShaftsPunctual, 0f) : 0f;
        marchShader.SetFloat("PunctualShafts", punctualShafts);
        marchShader.SetBool("PunctualUseShadows", fx.LightShaftsPunctualShadows);
        // Only upload + loop the punctual lights when the dial is on; otherwise zero the
        // counts so the shader skips the whole punctual path (the physical fog is untouched).
        if (punctualShafts > 0f && uploadPunctual is not null)
            uploadPunctual(marchShader);
        else {
            marchShader.SetInt("PointLightCount", 0);
            marchShader.SetInt("SpotLightCount", 0);
        }
        // March only the air the shadow map actually covers, so every sample has real shadow
        // data and the shaft contrast survives. Air past this reads "lit" (no data) and would
        // wash the effect flat.
        marchShader.SetFloat("MaxDistance",
            Math.Min(Math.Max(fx.VolumetricMaxDistance, 1f), Math.Max(shadowDistance, 1f)));
        GLBufferUtilities.DrawFullscreenQuad();

        // ---- 2. Temporal accumulate (writes the new history) ----
        historyWriteTex.BindAsTarget();
        temporalShader.Activate();
        BindTex(0, scatterTarget.Texture, temporalShader, "currentScatter");
        BindTex(1, historyRead.Texture, temporalShader, "historyScatter");
        BindTex(2, depthTexture, temporalShader, "depthTexture");
        temporalShader.SetMatrix4("InvProjection", ref invProjection);
        temporalShader.SetMatrix4("InvViewMatrix", ref invView);
        temporalShader.SetMatrix4("PrevViewProjection", ref prevViewProjection[targetIndex]);
        temporalShader.SetBool("HasHistory", hasHistory[targetIndex]);
        temporalShader.SetFloat("Feedback", Math.Clamp(fx.VolumetricFeedback, 0f, 0.98f));
        GLBufferUtilities.DrawFullscreenQuad();

        // ---- 3. Combine over the full-res scene ----
        combinedTarget.BindAsTarget();
        combineShader.Activate();
        BindTex(0, colorTexture, combineShader, "sceneTexture");
        BindTex(1, historyWriteTex.Texture, combineShader, "scatterTexture");
        BindTex(2, depthTexture, combineShader, "depthTexture");
        BindTex(3, depthTexture, combineShader, "scatterDepth");
        combineShader.SetMatrix4("InvProjection", ref invProjection);
        // Fog on: its master Intensity + tint, and it extinguishes the scene (real fog hides
        // things). Shafts-only: the LightShafts master Intensity, neutral tint, and NO
        // extinction — beams are additive glow over a clear scene, not a fog veil.
        combineShader.SetFloat("Intensity",
            fogOn ? Math.Max(fx.VolumetricIntensity, 0f) : Math.Max(fx.LightShaftsIntensity, 0f));
        combineShader.SetFloat3("Tint", fogOn ? fx.VolumetricTint : OpenTK.Mathematics.Vector3.One);
        combineShader.SetBool("Extinguish", fogOn);
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

    // The cascade array is a depth texture array sampled as sampler2DArrayShadow (the
    // compare mode is baked into the texture's parameters, not the bind).
    static void BindShadow(int unit, int texture, StandardShader shader, string name) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2DArray, texture);
        shader.SetInt(name, unit);
    }
}
