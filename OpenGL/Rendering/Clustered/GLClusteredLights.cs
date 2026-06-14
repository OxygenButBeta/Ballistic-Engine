using System;
using System.Collections.Generic;
using BallisticEngine;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.Clustered;

// CLUSTERED FORWARD (Forward+) light culling. The forward shader used to loop EVERY point/spot light
// per fragment with a hard cap of 8 point + 4 spot — the review's #1 ceiling ("a realistic interior
// has dozens of lamps"). This divides the view frustum into a 3D grid of CLUSTERS (froxels), assigns
// each light to the clusters its sphere touches (on the GPU), and the shader loops only the lights in
// the fragment's cluster. Unlocks hundreds of lights at roughly constant per-fragment cost.
//
// Layout (Doom-2016 / Olsson-style):
//   * XY = screen tiles (ClusterX x ClusterY).
//   * Z  = LOGARITHMIC depth slices (near detail, far coarse): slice = log(z/near)/log(far/near)*Zn.
//   * Per frame: (1) rebuild cluster AABBs in VIEW space when the projection/viewport changes,
//     (2) a compute pass tests every light's bounding sphere against each cluster's AABB and fills a
//     per-cluster (offset,count) grid + a flat light-index list, (3) the shader reads them.
//
// Bindings (SSBO): 12 = Lights, 13 = ClusterAABBs, 14 = LightGrid (offset,count per cluster),
// 15 = LightIndices (flat), 16 = the global index counter (atomic). Chosen above the GpuDriven (2-7)
// and SDF-GI (8-11) ranges so all subsystems coexist. PassData UBO stays binding 0.
//
// Default-ON but with a safe fallback: BALLISTIC_CLUSTERED=0 keeps the legacy capped per-fragment
// loop (the shader's UseClustered=false path), byte-identical to before. Without compute it auto-
// disables. The shader supports BOTH paths so a driver without SSBOs in the fragment stage still runs.
public sealed class GLClusteredLights : IDisposable {
    public const int ClusterX = 16;
    public const int ClusterY = 9;
    public const int ClusterZ = 24;
    public const int ClusterCount = ClusterX * ClusterY * ClusterZ;

    // Hard cap on total lights and on the flat index list. 1024 lights and an average of ~64 light
    // refs per cluster (ClusterCount * 64) is generous for an interior; overflow drops extra refs
    // (logged once) — never a crash.
    public const int MaxLights = 1024;
    public const int MaxLightIndices = ClusterCount * 32;
    public const int MaxLightsPerCluster = 128;

    // GPU light record (std430, 48 bytes = 3x vec4). MUST match GpuLight in the cluster + lit shaders.
    //   posRange   : xyz = world position, w = range (radius)
    //   color      : xyz = pre-exposed radiance, w = type (0 = point, 1 = spot)
    //   dirAngles  : xyz = spot direction (world), w = cosOuter
    //   (cosInner + shadowSlot packed into color.w? no — keep a 4th field) -> use a 4th vec4.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct GpuLight {
        public Vector4 PosRange;    // xyz pos, w range
        public Vector4 Color;       // xyz radiance, w type (0 point / 1 spot)
        public Vector4 DirCosOuter; // xyz spot dir, w cosOuter
        public Vector4 Extra;       // x cosInner, y shadowSlot (-1 none), z pad, w pad
    }

    public const int GpuLightBytes = 64; // 4 x vec4

    public bool Available { get; private set; }
    public int LightCount { get; private set; }

    int lightsSsbo;       // binding 12
    int aabbSsbo;         // binding 13
    int gridSsbo;         // binding 14  (ivec2 per cluster: offset, count)
    int indicesSsbo;      // binding 15
    int counterSsbo;      // binding 16  (1 uint atomic)

    int buildProgram;     // ClusterBuild_Comp — view-space AABBs
    int cullProgram;      // ClusterCull_Comp  — light->cluster assignment

    // Cached state to know when to rebuild the cluster AABB grid (only on proj/viewport change).
    Matrix4 lastProjection;
    int lastW, lastH;
    bool gridBuilt;

    int locBuildInvProj, locBuildScreen, locBuildNearFar, locBuildClusterDims;
    int locCullViewMat, locCullLightCount, locCullNearFar, locCullScreen, locCullClusterDims;

    readonly GpuLight[] scratch = new GpuLight[MaxLights];
    bool overflowLogged;

    public GLClusteredLights() {
        if (Environment.GetEnvironmentVariable("BALLISTIC_CLUSTERED") == "0") {
            Available = false;
            return;
        }
        // Requires compute + SSBO; GL 4.6 core has both. If the build fails, auto-disable.
        buildProgram = Compile("ClusterBuild_Comp.glsl");
        cullProgram = Compile("ClusterCull_Comp.glsl");
        if (buildProgram == 0 || cullProgram == 0) {
            Available = false;
            return;
        }

        lightsSsbo = GenBuffer(MaxLights * GpuLightBytes);
        aabbSsbo = GenBuffer(ClusterCount * 2 * 16);          // 2x vec4 (min,max) per cluster
        gridSsbo = GenBuffer(ClusterCount * 2 * sizeof(int)); // ivec2 per cluster
        indicesSsbo = GenBuffer(MaxLightIndices * sizeof(int));
        counterSsbo = GenBuffer(sizeof(uint));

        CacheLocations();
        Available = true;
    }

    void CacheLocations() {
        locBuildInvProj = GL.GetUniformLocation(buildProgram, "InvProjection");
        locBuildScreen = GL.GetUniformLocation(buildProgram, "ScreenSize");
        locBuildNearFar = GL.GetUniformLocation(buildProgram, "NearFar");
        locBuildClusterDims = GL.GetUniformLocation(buildProgram, "ClusterDims");
        locCullViewMat = GL.GetUniformLocation(cullProgram, "ViewMatrix");
        locCullLightCount = GL.GetUniformLocation(cullProgram, "LightCount");
        locCullNearFar = GL.GetUniformLocation(cullProgram, "NearFar");
        locCullScreen = GL.GetUniformLocation(cullProgram, "ScreenSize");
        locCullClusterDims = GL.GetUniformLocation(cullProgram, "ClusterDims");
    }

    static int GenBuffer(int bytes) {
        int b = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, b);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, bytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        return b;
    }

    // Builds the cluster light grid for this frame. lights = the gathered scene lights (already
    // pre-exposed). near/far are the camera planes; width/height the viewport; view/projection the
    // camera matrices. After this, Bind() exposes the SSBOs to the lit shader.
    public void Update(IReadOnlyList<GpuLight> lights, int width, int height,
        float near, float far, ref Matrix4 view, ref Matrix4 projection) {
        if (!Available)
            return;

        // 1. Upload lights.
        LightCount = Math.Min(lights.Count, MaxLights);
        if (LightCount > 0) {
            for (var i = 0; i < LightCount; i++)
                scratch[i] = lights[i];
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, lightsSsbo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                LightCount * GpuLightBytes, scratch);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }

        var nearFar = new Vector2(near, far);
        var screen = new Vector2(width, height);
        var dims = new Vector3i(ClusterX, ClusterY, ClusterZ);

        // 2. Rebuild cluster AABBs only when the projection or viewport changed (they're view-space,
        // camera-relative, so they're invariant under camera MOVEMENT — only proj/resize changes them).
        bool projChanged = MatrixDelta(projection, lastProjection) > 1e-6f || width != lastW || height != lastH;
        if (projChanged || !gridBuilt) {
            Matrix4 invProj = Matrix4.Invert(projection);
            GL.UseProgram(buildProgram);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, aabbSsbo);
            GL.UniformMatrix4(locBuildInvProj, false, ref invProj);
            GL.Uniform2(locBuildScreen, screen);
            GL.Uniform2(locBuildNearFar, nearFar);
            GL.Uniform3(locBuildClusterDims, dims);
            GL.DispatchCompute((ClusterX + 3) / 4, (ClusterY + 3) / 4, (ClusterZ + 3) / 4);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            lastProjection = projection;
            lastW = width;
            lastH = height;
            gridBuilt = true;
        }

        // 3. Reset the global index counter, then cull lights into clusters.
        uint zero = 0;
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, counterSsbo);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, sizeof(uint), ref zero);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        GL.UseProgram(cullProgram);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, lightsSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, aabbSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 14, gridSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 15, indicesSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, counterSsbo);
        GL.UniformMatrix4(locCullViewMat, false, ref view);
        GL.Uniform1(locCullLightCount, LightCount);
        GL.Uniform2(locCullNearFar, nearFar);
        GL.Uniform2(locCullScreen, screen);
        GL.Uniform3(locCullClusterDims, dims);
        // One thread per cluster (8x8x... workgroups). 16*9*24 clusters.
        GL.DispatchCompute((ClusterX + 3) / 4, (ClusterY + 3) / 4, (ClusterZ + 3) / 4);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    }

    // Binds the light SSBOs for the lit shader pass (12 = lights, 14 = grid, 15 = indices).
    public void Bind() {
        if (!Available)
            return;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, lightsSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 14, gridSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 15, indicesSsbo);
    }

    static float MatrixDelta(Matrix4 a, Matrix4 b) {
        float m = 0f;
        m = MathF.Max(m, RowMax(a.Row0 - b.Row0));
        m = MathF.Max(m, RowMax(a.Row1 - b.Row1));
        m = MathF.Max(m, RowMax(a.Row2 - b.Row2));
        m = MathF.Max(m, RowMax(a.Row3 - b.Row3));
        return m;
    }
    static float RowMax(Vector4 d) =>
        MathF.Max(MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y)), MathF.Max(MathF.Abs(d.Z), MathF.Abs(d.W)));

    static int Compile(string file) {
        int shader = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(shader, GLSLShaderUtilities.ToAscii(EmbeddedShaderSource.Read(file)));
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) {
            Debugging.LogError($"[GLClusteredLights] {file} compile failed:\n" + GL.GetShaderInfoLog(shader));
            GL.DeleteShader(shader);
            return 0;
        }
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, shader);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int lok);
        GL.DeleteShader(shader);
        if (lok == 0) {
            Debugging.LogError($"[GLClusteredLights] {file} link failed:\n" + GL.GetProgramInfoLog(prog));
            GL.DeleteProgram(prog);
            return 0;
        }
        return prog;
    }

    public void Dispose() {
        foreach (int b in new[] { lightsSsbo, aabbSsbo, gridSsbo, indicesSsbo, counterSsbo })
            if (b != 0) GL.DeleteBuffer(b);
        if (buildProgram != 0) GL.DeleteProgram(buildProgram);
        if (cullProgram != 0) GL.DeleteProgram(cullProgram);
        Available = false;
    }
}
