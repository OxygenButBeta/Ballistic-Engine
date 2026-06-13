using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.VoxelGI;

// Voxel Cone Tracing global illumination (UE5/Lumen-class diffuse + glossy indirect look).
// Owns the 3D radiance texture and the voxelization pass; the cone trace itself lives in the
// forward Frag.glsl (VoxelConeTrace.glsl injected). Pipeline per (re)voxelize:
//   1. clear the 3D texture
//   2. rasterize the scene with an axis-dominant ortho projection (Voxelize_*.glsl), each fragment
//      imageStore-ing its direct-lit radiance into the matching voxel
//   3. generate the mip chain (trilinear filter) so cones sample coarser radiance at distance
// Static scene => voxelize once (stamped); re-voxelize when geometry or the sun changes.
//
// Opt-in BALLISTIC_VOXELGI=1 while A/B'd against the baked-probe ambient; default off until it
// clearly beats the flat look, then flipped on.
public sealed class GLVoxelGI : IDisposable {
    public int VoxelRes { get; }
    public int RadianceTexture { get; private set; }
    public Vector3 VolumeMin { get; private set; }
    public Vector3 VolumeSize { get; private set; }
    public float VoxelWorldSize => VolumeSize.X / VoxelRes;
    public bool Available { get; private set; }

    int voxelizeProgram;            // vert+geom+frag (geometry stage = dominant-axis ortho)
    int fbo;                        // empty FBO: voxelization writes via imageStore, not color
    long lastStamp = -1;

    // Cached uniform locations for the voxelize program.
    int uVolumeMin, uVolumeInvSize, uVoxelRes, uSunDir, uSunColor, uSkyAmbient, uCascadeCount,
        uCascadeBias, uShadowCascades, uBouncePass, uVoxelSampler;
    readonly int[] uCascadeMatrices = new int[4];

    // Extra GI bounce passes after the direct pass (each compounds one more bounce). 0..2.
    public int BouncePasses { get; set; } = 2;

    void CacheUniforms() {
        int L(string n) => GL.GetUniformLocation(voxelizeProgram, n);
        uVolumeMin = L("VolumeMin"); uVolumeInvSize = L("VolumeInvSize"); uVoxelRes = L("VoxelRes");
        uSunDir = L("SunDir"); uSunColor = L("SunColor"); uSkyAmbient = L("SkyAmbient");
        uCascadeCount = L("CascadeCount"); uCascadeBias = L("CascadeBias");
        uShadowCascades = L("ShadowCascades");
        uBouncePass = L("BouncePass"); uVoxelSampler = L("VoxelRadianceSampler");
        for (var i = 0; i < 4; i++) uCascadeMatrices[i] = L($"CascadeMatrices[{i}]");
    }

    public GLVoxelGI(int voxelRes = 128) {
        VoxelRes = voxelRes;
    }

    public void Initialize() {
        // The radiance volume: RGBA8, mipped. rgb = injected/bounced radiance, a = occupancy.
        RadianceTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, RadianceTexture);
        int mips = 1 + (int)System.MathF.Floor(System.MathF.Log2(VoxelRes));
        GL.TexStorage3D(TextureTarget3d.Texture3D, mips, SizedInternalFormat.Rgba8,
            VoxelRes, VoxelRes, VoxelRes);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new[] { 0f, 0f, 0f, 0f });
        GL.BindTexture(TextureTarget.Texture3D, 0);

        fbo = GL.GenFramebuffer();

        // 3-stage program (vert+geom+frag) compiled directly — StandardShader is vert+frag only.
        voxelizeProgram = BuildVoxelizeProgram();
        if (voxelizeProgram != 0)
            CacheUniforms();
        Available = voxelizeProgram != 0 && RadianceTexture != 0;
    }

    // True if the scene needs re-voxelizing (geometry/sun stamp changed).
    public bool NeedsRebuild(long stamp) => stamp != lastStamp;

    // Sets the world-space bounds the voxel grid spans (call before VoxelizeBegin).
    public void SetBounds(Vector3 min, Vector3 size) {
        VolumeMin = min;
        VolumeSize = size;
    }

    // Clears the radiance texture and sets up GL state for the voxelization draw. The caller then
    // issues the scene draw (GPU-driven MDI) with `voxelizeShader` active, then calls VoxelizeEnd.
    public void VoxelizeBegin(long stamp,
        Vector3 sunDir, Vector3 sunColor, Vector3 skyAmbient,
        Matrix4[] cascadeMatrices, Vector4 cascadeBias, int cascadeCount, int shadowArrayTex) {
        lastStamp = stamp;

        // Clear the whole 3D texture (all mips get rebuilt after).
        GL.BindTexture(TextureTarget.Texture3D, RadianceTexture);
        GL.ClearTexImage(RadianceTexture, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);

        // Empty FBO sized to the voxel res — no color attachment; the frag writes via imageStore.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultWidth, VoxelRes);
        GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultHeight, VoxelRes);
        GL.Viewport(0, 0, VoxelRes, VoxelRes);

        // No depth, no cull (capture both faces), no color writes.
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.ColorMask(false, false, false, false);

        GL.UseProgram(voxelizeProgram);
        var invSize = new Vector3(1f / VolumeSize.X, 1f / VolumeSize.Y, 1f / VolumeSize.Z);
        GL.Uniform3(uVolumeMin, VolumeMin.X, VolumeMin.Y, VolumeMin.Z);
        GL.Uniform3(uVolumeInvSize, invSize.X, invSize.Y, invSize.Z);
        GL.Uniform1(uVoxelRes, VoxelRes);
        GL.Uniform3(uSunDir, sunDir.X, sunDir.Y, sunDir.Z);
        GL.Uniform3(uSunColor, sunColor.X, sunColor.Y, sunColor.Z);
        GL.Uniform3(uSkyAmbient, skyAmbient.X, skyAmbient.Y, skyAmbient.Z);
        GL.Uniform1(uCascadeCount, cascadeCount);
        GL.Uniform4(uCascadeBias, cascadeBias.X, cascadeBias.Y, cascadeBias.Z, cascadeBias.W);
        for (var i = 0; i < 4; i++) {
            Matrix4 cm = cascadeMatrices[i];
            GL.UniformMatrix4(uCascadeMatrices[i], false, ref cm);
        }
        // Shadow cascades on unit 10.
        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture2DArray, shadowArrayTex);
        GL.Uniform1(uShadowCascades, 10);

        // Direct pass (overwrite). RMW image so the bounce passes can read-modify.
        GL.Uniform1(uBouncePass, 0);
        GL.BindImageTexture(0, RadianceTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba8);

        // Per-draw + material SSBOs are bound by the GPU-driven DrawIndirectCount the caller invokes.
    }

    // Between the direct draw and a bounce draw: mip the current radiance (so the bounce reads the
    // hemisphere average from coarse mips), then set the bounce-pass state. The caller draws again.
    public void BeginBouncePass(int pass) {
        // Barrier the prior pass's image writes, then rebuild mips for the sampler reads.
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
        GL.BindTexture(TextureTarget.Texture3D, RadianceTexture);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);

        GL.UseProgram(voxelizeProgram);
        GL.Uniform1(uBouncePass, pass);
        // Sampler (unit 11) reads the just-mipped radiance; image unit 0 is the RMW target.
        GL.ActiveTexture(TextureUnit.Texture11);
        GL.BindTexture(TextureTarget.Texture3D, RadianceTexture);
        GL.Uniform1(uVoxelSampler, 11);
        GL.BindImageTexture(0, RadianceTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba8);
    }

    public int VoxelizeProgram => voxelizeProgram;

    // Finishes voxelization: barrier on the image writes, restore state, rebuild the mip chain.
    public void VoxelizeEnd() {
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
        GL.ColorMask(true, true, true, true);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Trilinear mip chain so cones can sample coarser radiance at distance.
        GL.BindTexture(TextureTarget.Texture3D, RadianceTexture);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
        GL.BindTexture(TextureTarget.Texture3D, 0);
    }

    static int BuildVoxelizeProgram() {
        int Compile(ShaderType type, string src) {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, GLSLShaderUtilities.ToAscii(src));
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) {
                Console.WriteLine($"[VoxelGI] {type} compile failed:\n{GL.GetShaderInfoLog(s)}");
                GL.DeleteShader(s);
                return 0;
            }
            return s;
        }
        int vs = Compile(ShaderType.VertexShader, EmbeddedShaderSource.Read("Voxelize_Vert.glsl"));
        int gs = Compile(ShaderType.GeometryShader, EmbeddedShaderSource.Read("Voxelize_Geom.glsl"));
        int fs = Compile(ShaderType.FragmentShader, EmbeddedShaderSource.Read("Voxelize_Frag.glsl"));
        if (vs == 0 || gs == 0 || fs == 0)
            return 0;
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vs); GL.AttachShader(prog, gs); GL.AttachShader(prog, fs);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int lok);
        GL.DeleteShader(vs); GL.DeleteShader(gs); GL.DeleteShader(fs);
        if (lok == 0) {
            Console.WriteLine($"[VoxelGI] link failed:\n{GL.GetProgramInfoLog(prog)}");
            GL.DeleteProgram(prog);
            return 0;
        }
        return prog;
    }

    public void Dispose() {
        if (RadianceTexture != 0) { GL.DeleteTexture(RadianceTexture); RadianceTexture = 0; }
        if (fbo != 0) { GL.DeleteFramebuffer(fbo); fbo = 0; }
        if (voxelizeProgram != 0) { GL.DeleteProgram(voxelizeProgram); voxelizeProgram = 0; }
        Available = false;
    }
}
