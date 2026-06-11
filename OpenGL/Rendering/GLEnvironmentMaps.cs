using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// Split-sum IBL baked on the GPU at runtime: a one-time BRDF integration LUT, and a
// per-skybox irradiance cubemap (cosine convolution) + GGX-prefiltered specular cubemap
// (roughness per mip). Each pass renders a fullscreen quad per cube face, reconstructing
// the face direction from the quad UV, so no cube geometry or view matrices are needed.
public static class GLEnvironmentMaps {
    public const int IrradianceSize = 32;
    public const int PrefilterSize = 128;
    public const int PrefilterMipCount = 5;

    static StandardShader brdfShader;
    static StandardShader irradianceShader;
    static StandardShader prefilterShader;
    static int fbo;

    static void EnsureResources() {
        if (brdfShader is not null)
            return;
        var vert = EmbeddedShaderSource.Read("FSQ_Vert.glsl");
        brdfShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("IBL_BrdfLut.glsl"));
        irradianceShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("IBL_Irradiance.glsl"));
        prefilterShader = GraphicAPI.CreateStandardShader(vert, EmbeddedShaderSource.Read("IBL_Prefilter.glsl"));
        fbo = GL.GenFramebuffer();
    }

    static void BeginBake() {
        EnsureResources();
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
    }

    static void EndBake() {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    public static int GenerateBrdfLut(int size = 512) {
        BeginBake();

        var lut = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, lut);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg16f, size, size, 0,
            PixelFormat.Rg, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, lut, 0);
        GL.Viewport(0, 0, size, size);
        brdfShader.Activate();
        GLBufferUtilities.DrawFullscreenQuad();

        EndBake();
        return lut;
    }

    public static int GenerateIrradiance(int sourceCubemap) {
        BeginBake();

        var cubemap = CreateCubemap(IrradianceSize, mipCount: 1);

        irradianceShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.TextureCubeMap, sourceCubemap);
        irradianceShader.SetInt("EnvironmentMap", 0);

        GL.Viewport(0, 0, IrradianceSize, IrradianceSize);
        for (var face = 0; face < 6; face++) {
            irradianceShader.SetInt("Face", face);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.TextureCubeMapPositiveX + face, cubemap, 0);
            GLBufferUtilities.DrawFullscreenQuad();
        }

        EndBake();
        return cubemap;
    }

    public static int GeneratePrefiltered(int sourceCubemap) {
        BeginBake();

        var cubemap = CreateCubemap(PrefilterSize, PrefilterMipCount);

        prefilterShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.TextureCubeMap, sourceCubemap);
        prefilterShader.SetInt("EnvironmentMap", 0);
        prefilterShader.SetFloat("SourceResolution", PrefilterSize * 4f);

        for (var mip = 0; mip < PrefilterMipCount; mip++) {
            var mipSize = PrefilterSize >> mip;
            GL.Viewport(0, 0, mipSize, mipSize);
            prefilterShader.SetFloat("Roughness", mip / (float)(PrefilterMipCount - 1));
            for (var face = 0; face < 6; face++) {
                prefilterShader.SetInt("Face", face);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.TextureCubeMapPositiveX + face, cubemap, mip);
                GLBufferUtilities.DrawFullscreenQuad();
            }
        }

        EndBake();
        return cubemap;
    }

    static int CreateCubemap(int size, int mipCount) {
        var cubemap = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, cubemap);
        for (var mip = 0; mip < mipCount; mip++) {
            var mipSize = Math.Max(1, size >> mip);
            for (var face = 0; face < 6; face++)
                GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, mip, PixelInternalFormat.Rgba16f,
                    mipSize, mipSize, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter,
            mipCount > 1 ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, mipCount - 1);
        return cubemap;
    }
}
