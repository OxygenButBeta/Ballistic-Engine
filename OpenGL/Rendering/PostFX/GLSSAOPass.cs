using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Screen-space ambient occlusion from the resolved depth buffer. Half resolution,
// hemisphere kernel with per-pixel rotation, followed by a 4x4 box blur. Normals are
// reconstructed from depth derivatives so the forward pass needs no extra G-buffer.
public sealed class GLSSAOPass {
    readonly StandardShader ssaoShader;
    readonly StandardShader blurShader;

    int aoTexture, blurTexture;
    int fbo;
    int width, height;

    public GLSSAOPass() {
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        ssaoShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSAO_Frag.glsl"));
        blurShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("SSAO_Blur.glsl"));
    }

    void EnsureTargets(int sourceWidth, int sourceHeight) {
        if (fbo == 0)
            fbo = GL.GenFramebuffer();

        var w = Math.Max(1, sourceWidth / 2);
        var h = Math.Max(1, sourceHeight / 2);
        if (aoTexture != 0 && w == width && h == height)
            return;

        width = w;
        height = h;
        if (aoTexture != 0) {
            GL.DeleteTexture(aoTexture);
            GL.DeleteTexture(blurTexture);
        }

        aoTexture = CreateTarget(w, h);
        blurTexture = CreateTarget(w, h);
    }

    static int CreateTarget(int w, int h) {
        var tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, w, h, 0,
            PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return tex;
    }

    // Returns the blurred AO texture (white = unoccluded), or 0 when unavailable.
    public int Render(int depthTexture, int sourceWidth, int sourceHeight, Matrix4 projection,
        PostProcessSettings fx) {
        if (depthTexture <= 0)
            return 0;

        EnsureTargets(sourceWidth, sourceHeight);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        // AO estimate.
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, aoTexture, 0);
        GL.Viewport(0, 0, width, height);

        ssaoShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        ssaoShader.SetInt("depthTexture", 0);

        Matrix4 invProjection = Matrix4.Invert(projection);
        ssaoShader.SetMatrix4("Projection", ref projection);
        ssaoShader.SetMatrix4("InvProjection", ref invProjection);
        ssaoShader.SetFloat("Radius", fx.SSAORadius);
        ssaoShader.SetFloat("Intensity", fx.SSAOIntensity);
        ssaoShader.SetFloat2("TexelSize", new Vector2(1f / width, 1f / height));
        GLBufferUtilities.DrawFullscreenQuad();

        // Noise-hiding DEPTH-AWARE (bilateral) blur — weights taps by depth similarity so AO doesn't
        // smear across silhouettes (the halo a plain box blur leaves around objects).
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, blurTexture, 0);
        blurShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, aoTexture);
        blurShader.SetInt("aoTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        blurShader.SetInt("depthTexture", 1);
        blurShader.SetMatrix4("InvProjection", ref invProjection);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return blurTexture;
    }
}
