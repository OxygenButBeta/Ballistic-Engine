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
    public void Render(GLFrameBuffer source, GLFrameBuffer destination, int destWidth, int destHeight,
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
        GL.BindTexture(TextureTarget.Texture2D, source.colorBuffer);
        shader.SetInt("hdrTexture", 31);

        GL.ActiveTexture(TextureUnit.Texture30);
        GL.BindTexture(TextureTarget.Texture2D, bloomTexture);
        shader.SetInt("bloomTexture", 30);

        GL.ActiveTexture(TextureUnit.Texture29);
        GL.BindTexture(TextureTarget.Texture2D, aoTexture);
        shader.SetInt("aoTexture", 29);

        shader.SetFloat("Exposure", fx.Exposure);
        shader.SetFloat("BloomIntensity", bloomTexture != 0 ? fx.BloomIntensity : 0f);
        shader.SetBool("ApplyAO", aoTexture != 0);
        shader.SetFloat("Contrast", fx.Contrast);
        shader.SetFloat("Saturation", fx.Saturation);
        shader.SetFloat("VignetteStrength", fx.VignetteStrength);
        shader.SetFloat("FilmGrain", fx.FilmGrain);
        shader.SetFloat("Sharpen", fx.Sharpen);

        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
    }
}
