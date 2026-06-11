using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// Final HDR -> display pass: AO multiply, bloom add, exposure, ACES tonemap, optional
// grade extras, gamma. Writes into a destination framebuffer (editor display targets)
// or the default framebuffer (player present).
public sealed class GLCompositePass {
    readonly StandardShader shader;

    public GLCompositePass() {
        shader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("FSQ_Frag.glsl"));
    }

    // bloomTexture/aoTexture of 0 disable the respective term.
    public void Render(int sourceTexture, GLFrameBuffer destination, int destWidth, int destHeight,
        PostProcessSettings fx, int bloomTexture, int aoTexture) {
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);

        if (destination is null)
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        else
            destination.Activate();
        GL.Viewport(0, 0, destWidth, destHeight);

        shader.Activate();

        GL.ActiveTexture(TextureUnit.Texture31);
        GL.BindTexture(TextureTarget.Texture2D, sourceTexture);
        shader.SetInt("hdrTexture", 31);

        GL.ActiveTexture(TextureUnit.Texture30);
        GL.BindTexture(TextureTarget.Texture2D, bloomTexture);
        shader.SetInt("bloomTexture", 30);

        GL.ActiveTexture(TextureUnit.Texture29);
        GL.BindTexture(TextureTarget.Texture2D, aoTexture);
        shader.SetInt("aoTexture", 29);

        // The HDR buffer is PRE-EXPOSED (the EV exposure is applied to the light uniforms at
        // the source - see GLHDRenderer's pre-exposure note), so the tonemap takes it as-is.
        // Applying ExposureMultiplier here too would double-expose to black.
        shader.SetFloat("Exposure", 1f);
        shader.SetFloat("BloomIntensity", bloomTexture != 0 ? fx.BloomIntensity : 0f);
        shader.SetBool("ApplyAO", aoTexture != 0);
        // Composite AO is a blend amount (0..1); the SSAO pass's Intensity already controls
        // how dark the occlusion itself gets, so clamp here to avoid compounding the two.
        shader.SetFloat("AoStrength", Math.Min(fx.SSAOIntensity, 1f));
        shader.SetFloat("Contrast", fx.Contrast);
        shader.SetFloat("Saturation", fx.Saturation);
        shader.SetFloat("VignetteStrength", fx.VignetteStrength);
        shader.SetFloat("VignetteRoundness", fx.VignetteRoundness);
        shader.SetFloat3("VignetteColor", fx.VignetteColor);
        shader.SetFloat("Aspect", destHeight != 0 ? (float)destWidth / destHeight : 1f);
        shader.SetFloat("FilmGrain", fx.FilmGrain);
        shader.SetFloat("Sharpen", fx.Sharpen);
        shader.SetFloat("ChromaticAberration", fx.ChromaticAberration);
        shader.SetFloat("LensDistortion", fx.LensDistortion);

        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
    }
}
