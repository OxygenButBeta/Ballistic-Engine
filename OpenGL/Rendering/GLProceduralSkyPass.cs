using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Bakes the ProceduralSky component into an RGBA16F cubemap (one fullscreen pass per face,
// FaceDir convention shared with the IBL bakes). Re-bakes only when the sun direction, an
// atmosphere/cloud parameter, or the quantized cloud-wind time changes, then bumps the
// wrapper's ContentVersion so the renderer re-convolves the IBL maps. skyAmbient comes from
// averaging the top mip after the bake.
public sealed class GLProceduralSkyPass {
    public readonly GLRuntimeCubemap Cubemap = new();

    StandardShader shader;
    int fbo;
    int cubemapId;
    int resolution;
    int paramStamp;

    public void EnsureBaked(ProceduralSky sky, Vector3 sunDirection, Vector3 sunRadiance,
        float sunAngularRadius) {
        var res = Math.Clamp(sky.Resolution, 16, 1024);

        // Animated clouds: quantizing time into the hash re-bakes once per interval instead
        // of every frame (each re-bake also re-convolves the IBL, so the cadence is opt-in).
        var cloudTime = 0f;
        if (sky.CloudsEnabled && sky.CloudUpdateInterval > 0f && sky.CloudWindSpeed != 0f)
            cloudTime = MathF.Floor((float)Time.TotalTime / sky.CloudUpdateInterval)
                        * sky.CloudUpdateInterval;

        var hash = new HashCode();
        hash.Add(MathF.Round(sunDirection.X, 4));
        hash.Add(MathF.Round(sunDirection.Y, 4));
        hash.Add(MathF.Round(sunDirection.Z, 4));
        hash.Add(sunRadiance);
        hash.Add(sunAngularRadius);
        hash.Add(sky.Exposure);
        hash.Add(sky.AirDensity);
        hash.Add(sky.Haze);
        hash.Add(sky.HazeAnisotropy);
        hash.Add(sky.OzoneDensity);
        hash.Add(sky.GroundColor);
        hash.Add(sky.MultipleScattering);
        hash.Add(sky.SunDiskIntensity);
        hash.Add(sky.CloudsEnabled);
        hash.Add(sky.CloudCoverage);
        hash.Add(sky.CloudDensity);
        hash.Add(sky.CloudAltitude);
        hash.Add(sky.CloudThickness);
        hash.Add(sky.CloudScale);
        hash.Add(sky.CloudDetail);
        hash.Add(sky.CloudAmbient);
        hash.Add(sky.CloudWindSpeed);
        hash.Add(sky.CloudWindDirection);
        hash.Add(cloudTime);
        hash.Add(res);
        var stamp = hash.ToHashCode();
        if (stamp == paramStamp && Cubemap.UID != 0)
            return;
        paramStamp = stamp;

        EnsureResources(res);
        Bake(sky, sunDirection, sunRadiance, sunAngularRadius, cloudTime);
    }

    void EnsureResources(int res) {
        shader ??= GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("Sky_Procedural.glsl"));
        if (fbo == 0)
            fbo = GL.GenFramebuffer();

        if (cubemapId != 0 && resolution == res)
            return;
        if (cubemapId != 0)
            GL.DeleteTexture(cubemapId);

        resolution = res;
        cubemapId = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, cubemapId);
        for (var face = 0; face < 6; face++)
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, PixelInternalFormat.Rgba16f,
                res, res, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);
    }

    void Bake(ProceduralSky sky, Vector3 sunDirection, Vector3 sunRadiance, float sunAngularRadius,
        float cloudTime) {
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, resolution, resolution);

        shader.Activate();
        shader.SetFloat3("SunDirection", sunDirection.Normalized());
        shader.SetFloat3("SunRadiance", sunRadiance);
        shader.SetFloat("SunAngularRadius", MathF.Max(sunAngularRadius, 1e-4f));
        shader.SetFloat("SunDiskIntensity", MathF.Max(sky.SunDiskIntensity, 0f));
        shader.SetFloat("AirDensity", MathF.Max(sky.AirDensity, 0f));
        shader.SetFloat("Haze", MathF.Max(sky.Haze, 0f));
        shader.SetFloat("HazeAnisotropy", Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f));
        shader.SetFloat("OzoneDensity", MathF.Max(sky.OzoneDensity, 0f));
        shader.SetFloat3("GroundAlbedo", sky.GroundColor);
        shader.SetFloat("MultiScatter", MathF.Max(sky.MultipleScattering, 1f));
        shader.SetFloat("Exposure", MathF.Max(sky.Exposure, 0f));

        shader.SetInt("CloudsEnabled", sky.CloudsEnabled ? 1 : 0);
        shader.SetFloat("CloudCoverage", Math.Clamp(sky.CloudCoverage, 0f, 1f));
        shader.SetFloat("CloudDensity", MathF.Max(sky.CloudDensity, 0f));
        shader.SetFloat("CloudAltitude", Math.Clamp(sky.CloudAltitude, 600f, 20000f));
        shader.SetFloat("CloudThickness", Math.Clamp(sky.CloudThickness, 100f, 20000f));
        shader.SetFloat("CloudScale", MathF.Max(sky.CloudScale, 0.05f));
        shader.SetFloat("CloudDetail", Math.Clamp(sky.CloudDetail, 0f, 1f));
        shader.SetFloat("CloudAmbient", MathF.Max(sky.CloudAmbient, 0f));
        var windRadians = MathHelper.DegreesToRadians(sky.CloudWindDirection);
        shader.SetFloat3("CloudWindOffset",
            new Vector3(MathF.Sin(windRadians), 0f, MathF.Cos(windRadians))
            * (sky.CloudWindSpeed * cloudTime));

        for (var face = 0; face < 6; face++) {
            shader.SetInt("Face", face);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.TextureCubeMapPositiveX + face, cubemapId, 0);
            GLBufferUtilities.DrawFullscreenQuad();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // skyAmbient = average radiance, read from the 1x1 top mip of each face.
        GL.BindTexture(TextureTarget.TextureCubeMap, cubemapId);
        GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);
        var topMip = (int)MathF.Log2(resolution);
        var texel = new float[4];
        Vector3 ambient = Vector3.Zero;
        for (var face = 0; face < 6; face++) {
            GL.GetTexImage(TextureTarget.TextureCubeMapPositiveX + face, topMip,
                PixelFormat.Rgba, PixelType.Float, texel);
            ambient += new Vector3(texel[0], texel[1], texel[2]);
        }
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
        GL.ActiveTexture(TextureUnit.Texture0);

        // Log the first bake only: animated clouds re-bake on a cadence and would spam.
        var firstBake = Cubemap.UID == 0;
        Cubemap.Adopt(cubemapId, ambient / 6f);
        if (firstBake)
            Console.WriteLine("[ProceduralSky] cubemap baked.");
    }
}
