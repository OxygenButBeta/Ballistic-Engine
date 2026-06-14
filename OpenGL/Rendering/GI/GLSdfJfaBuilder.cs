using System;
using BallisticEngine.GI;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GI;

// GPU JUMP-FLOOD (JFA) signed-distance-field builder for the Global Distance Field.
//
// The CPU full-res bake (MeshSdfBaker.BakePrepared) is ~1s per 96^3 cascade — too slow to keep the GDF
// fine under camera motion, so the field stays coarse and its blocky hit/miss structure is the GI
// speckle. This builder replaces the whole-grid CPU bake with: cheap CPU SEEDS on the surface shell
// (SdfSeedExtractor, ~ms — surface-area cost, not volume) -> upload -> log2(res) GPU jump-flood passes
// -> a resolve pass that writes the SAME R16F signed-world-distance + RGBA8 albedo the CPU produced.
// The flood is the standard GPU SDF algorithm; the SIGN is the proven CPU 7-ray parity carried on the
// seeds (no new sign method, no all-teal-class risk).
//
// Owns the ping-pong seed textures (RGBA32F: xyz=surface point in voxel coords, w=sign) + a parallel
// albedo ping-pong (RGBA16F), and the JFA + resolve compute programs. Build() floods into caller-owned
// distance (R16F) + albedo (RGBA8) 3D textures, sized res^3, that the march/inject already read.
public sealed class GLSdfJfaBuilder : IDisposable {
    readonly int res;
    readonly int seedA, seedB;       // RGBA32F nearest-seed ping-pong
    readonly int albA, albB;         // RGBA16F nearest-seed-albedo ping-pong
    readonly int jfaProgram;
    readonly int resolveProgram;

    int jfaRes, jfaStep, jfaResolveRes, jfaCellWorld, jfaFarDist;

    public bool Available => jfaProgram != 0 && resolveProgram != 0;

    public GLSdfJfaBuilder(int resolution) {
        res = Math.Clamp(resolution, 8, 256);
        seedA = CreateTex(SizedInternalFormat.Rgba32f);
        seedB = CreateTex(SizedInternalFormat.Rgba32f);
        albA = CreateTex(SizedInternalFormat.Rgba16f);
        albB = CreateTex(SizedInternalFormat.Rgba16f);

        jfaProgram = CompileCompute(EmbeddedShaderSource.Read("JFA_Comp.glsl"), "JFA_Comp");
        resolveProgram = CompileCompute(EmbeddedShaderSource.Read("SdfResolve_Comp.glsl"), "SdfResolve_Comp");
        if (jfaProgram != 0) {
            jfaRes = GL.GetUniformLocation(jfaProgram, "Res");
            jfaStep = GL.GetUniformLocation(jfaProgram, "Step");
        }
        if (resolveProgram != 0) {
            jfaResolveRes = GL.GetUniformLocation(resolveProgram, "Res");
            jfaCellWorld = GL.GetUniformLocation(resolveProgram, "CellWorld");
            jfaFarDist = GL.GetUniformLocation(resolveProgram, "FarDist");
        }
    }

    int CreateTex(SizedInternalFormat fmt) {
        int t = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, t);
        GL.TexStorage3D(TextureTarget3d.Texture3D, 1, fmt, res, res, res);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        return t;
    }

    static int CompileCompute(string src, string name) {
        int sh = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(sh, GLSLShaderUtilities.ToAscii(src)); // em-dash sanitize (CLAUDE.md gotcha)
        GL.CompileShader(sh);
        GL.GetShader(sh, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) {
            Debugging.LogError($"[GLSdfJfaBuilder] {name} compile failed:\n" + GL.GetShaderInfoLog(sh));
            GL.DeleteShader(sh); return 0;
        }
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, sh);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int lok);
        GL.DeleteShader(sh);
        if (lok == 0) {
            Debugging.LogError($"[GLSdfJfaBuilder] {name} link failed:\n" + GL.GetProgramInfoLog(prog));
            GL.DeleteProgram(prog); return 0;
        }
        return prog;
    }

    // Build the signed distance + albedo field from a seed grid into the caller's distOut (R16F) and
    // albedoOut (RGBA8) 3D textures (both res^3). cellWorld = world metres per cubic cell. farDist =
    // the value written where no seed reached. Runs entirely on the GL thread (the seed upload + the
    // log2(res) flood passes + the resolve). Returns false if unavailable.
    public bool Build(SdfSeedExtractor.SeedGrid seeds, int distOut, int albedoOut, float cellWorld, float farDist) {
        if (!Available || seeds == null)
            return false;

        // ---- Upload the seeds into seedA (and their albedo into albA) ----
        // SeedPos is Vector4[] x-fastest -> RGBA32F. Albedo is float[] RGB -> we widen to RGBA16F here.
        UploadSeed(seedA, seeds);
        UploadAlbedo(albA, seeds);

        int src = seedA, dst = seedB;
        int srcAlb = albA, dstAlb = albB;
        int g = (res + 3) / 4;

        GL.UseProgram(jfaProgram);
        GL.Uniform1(jfaRes, res);

        // JFA passes: step = res/2, res/4, ..., 1. (1+JFA — a final step-1 pass after the halving chain
        // cleans the rare medial-axis error; included by starting the chain and ending at 1.)
        for (int step = res / 2; step >= 1; step /= 2) {
            GL.Uniform1(jfaStep, step);
            BindSampler(0, src);
            GL.BindImageTexture(1, dst, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba32f);
            BindSampler(2, srcAlb);
            GL.BindImageTexture(3, dstAlb, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
            GL.DispatchCompute(g, g, g);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
            (src, dst) = (dst, src);
            (srcAlb, dstAlb) = (dstAlb, srcAlb);
        }

        // ---- Resolve: nearest-seed field (now in `src`) -> signed distance + albedo ----
        GL.UseProgram(resolveProgram);
        GL.Uniform1(jfaResolveRes, res);
        GL.Uniform1(jfaCellWorld, cellWorld);
        GL.Uniform1(jfaFarDist, farDist);
        BindSampler(0, src);
        GL.BindImageTexture(1, distOut, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);
        BindSampler(2, srcAlb);
        GL.BindImageTexture(3, albedoOut, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba8);
        GL.DispatchCompute(g, g, g);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
        return true;
    }

    void UploadSeed(int tex, SdfSeedExtractor.SeedGrid seeds) {
        GL.BindTexture(TextureTarget.Texture3D, tex);
        // Vector4[] is x-fastest, contiguous floats -> RGBA32F TexSubImage.
        var flat = new float[seeds.SeedPos.Length * 4];
        for (int i = 0; i < seeds.SeedPos.Length; i++) {
            Vector4 s = seeds.SeedPos[i];
            flat[i * 4] = s.X; flat[i * 4 + 1] = s.Y; flat[i * 4 + 2] = s.Z; flat[i * 4 + 3] = s.W;
        }
        GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, res, res, res,
            PixelFormat.Rgba, PixelType.Float, flat);
        GL.BindTexture(TextureTarget.Texture3D, 0);
    }

    void UploadAlbedo(int tex, SdfSeedExtractor.SeedGrid seeds) {
        GL.BindTexture(TextureTarget.Texture3D, tex);
        // Albedo is RGB float -> widen to RGBA16F (a=1). The seed albedo is only meaningful at seeds;
        // the flood copies the winning seed's albedo so non-seed voxels fill in.
        int n = res * res * res;
        var rgba = new float[n * 4];
        for (int i = 0; i < n; i++) {
            rgba[i * 4] = seeds.Albedo[i * 3];
            rgba[i * 4 + 1] = seeds.Albedo[i * 3 + 1];
            rgba[i * 4 + 2] = seeds.Albedo[i * 3 + 2];
            rgba[i * 4 + 3] = 1f;
        }
        GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, res, res, res,
            PixelFormat.Rgba, PixelType.Float, rgba);
        GL.BindTexture(TextureTarget.Texture3D, 0);
    }

    static void BindSampler(int unit, int tex) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture3D, tex);
    }

    // CORRECTNESS HARNESS (Phase 1, BALLISTIC_JFA_SELFTEST=1). Builds the JFA distance field for a
    // prepared snapshot, reads it back, and compares VOXEL-BY-VOXEL against the proven CPU BakePrepared
    // field over the SAME box/res. Prints distance MAE/max + sign-mismatch fraction so the GPU path is
    // proven to match the CPU ground truth BEFORE it touches the renderer (the sign de-risk). One-shot.
    public void SelfTest(MeshSdfBaker.PreparedField prep, Vector3 boundsMin, Vector3 boundsMax, Vector3i res) {
        if (!Available || prep == null) { Console.WriteLine("[JFA selftest] unavailable"); return; }
        int n = this.res;
        if (res.X != n || res.Y != n || res.Z != n) {
            Console.WriteLine($"[JFA selftest] res mismatch (builder {n}^3 vs request {res})"); return;
        }
        float cellWorld = (boundsMax.X - boundsMin.X) / n;
        float farDist = cellWorld * n * 2f;

        // GPU field.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var seeds = SdfSeedExtractor.Extract(prep, boundsMin, boundsMax, res, 7, 1.75f); // match runtime (7-ray, wide band)
        long seedMs = sw.ElapsedMilliseconds;
        int distTex = CreateTex(SizedInternalFormat.R16f);
        int albTex = CreateTex(SizedInternalFormat.Rgba8);
        sw.Restart();
        Build(seeds, distTex, albTex, cellWorld, farDist);
        GL.Finish();
        long gpuMs = sw.ElapsedMilliseconds;

        var gpu = new float[n * n * n];
        GL.BindTexture(TextureTarget.Texture3D, distTex);
        GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.Float, gpu);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        // CPU ground truth (full 7-ray sign).
        sw.Restart();
        MeshSdf cpu = MeshSdfBaker.BakePrepared(prep, boundsMin, boundsMax, res, out _, 7);
        long cpuMs = sw.ElapsedMilliseconds;

        // Compare over the NEAR-SURFACE band (|cpu| < 3 cells) — that's what the march/inject actually
        // read; JFA's worst error is far from seeds, harmless there. Also report whole-grid for honesty.
        double sumAbsBand = 0, maxBand = 0; int nBand = 0, signMismatchBand = 0;
        double sumAbsAll = 0, maxAll = 0; int signMismatchAll = 0;
        float bandWorld = 3f * cellWorld;
        for (int i = 0; i < gpu.Length; i++) {
            float g = gpu[i], c = cpu.Distances[i];
            float e = MathF.Abs(g - c);
            sumAbsAll += e; if (e > maxAll) maxAll = e;
            if ((g < 0f) != (c < 0f)) signMismatchAll++;
            if (MathF.Abs(c) < bandWorld) {
                sumAbsBand += e; if (e > maxBand) maxBand = e; nBand++;
                if ((g < 0f) != (c < 0f)) signMismatchBand++;
            }
        }
        GL.DeleteTexture(distTex); GL.DeleteTexture(albTex);
        int total = gpu.Length;
        Console.WriteLine($"[JFA selftest] {n}^3, {prep.TriangleCount} tris, seeds={seeds.SeedCount}");
        Console.WriteLine($"[JFA selftest] timings: seed {seedMs}ms + gpu {gpuMs}ms  vs  cpu {cpuMs}ms");
        Console.WriteLine($"[JFA selftest] BAND(|cpu|<3cell, {nBand} vox): MAE={sumAbsBand / Math.Max(1, nBand):F4}m  max={maxBand:F4}m  cell={cellWorld:F4}m  signMiss={100.0 * signMismatchBand / Math.Max(1, nBand):F2}%");
        Console.WriteLine($"[JFA selftest] ALL ({total} vox): MAE={sumAbsAll / total:F4}m  max={maxAll:F4}m  signMiss={100.0 * signMismatchAll / total:F2}%");
    }

    public void Dispose() {
        if (seedA != 0) GL.DeleteTexture(seedA);
        if (seedB != 0) GL.DeleteTexture(seedB);
        if (albA != 0) GL.DeleteTexture(albA);
        if (albB != 0) GL.DeleteTexture(albB);
        if (jfaProgram != 0) GL.DeleteProgram(jfaProgram);
        if (resolveProgram != 0) GL.DeleteProgram(resolveProgram);
    }
}
