using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12LtcTables : IDisposable {
    public const int N = 64;
    const int FitSamples = 32;

    readonly Dx12Device dev;
    ID3D12Resource ltc1, ltc2;
    int ltc1Srv = -1, ltc2Srv = -1;

    public CpuDescriptorHandle Ltc1SrvCpu => Dx12Backend.SrvStore.Cpu(ltc1Srv);
    public CpuDescriptorHandle Ltc2SrvCpu => Dx12Backend.SrvStore.Cpu(ltc2Srv);

    public Dx12LtcTables(Dx12Device device) {
        dev = device;
        if (!TryLoadCached(out float[] m, out float[] amp)) {
            BuildTables(out m, out amp);
            TrySaveCached(m, amp);
        }
        ltc1 = UploadRgba32f(m, "LtcMatInv");
        ltc2 = UploadRgba32f(amp, "LtcAmp");
        ltc1Srv = MakeSrv(ltc1);
        ltc2Srv = MakeSrv(ltc2);
    }

    const int FitVersion = 1;

    static string CachePath() {
        string shaderDir = Dx12ShaderCompiler.CacheDirectory;
        if (string.IsNullOrEmpty(shaderDir)) return null;
        string libDir = System.IO.Path.GetDirectoryName(shaderDir);
        return System.IO.Path.Combine(libDir, "LtcCache", $"ltc_v{FitVersion}_n{N}_s{FitSamples}.bin");
    }

    static bool TryLoadCached(out float[] mat, out float[] amp) {
        mat = amp = null;
        string path = CachePath();
        if (path is null || !System.IO.File.Exists(path)) return false;
        try {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            int n = N * N * 4;
            if (bytes.Length != n * 2 * sizeof(float)) return false;
            mat = new float[n]; amp = new float[n];
            Buffer.BlockCopy(bytes, 0, mat, 0, n * sizeof(float));
            Buffer.BlockCopy(bytes, n * sizeof(float), amp, 0, n * sizeof(float));
            return true;
        } catch { mat = amp = null; return false; }
    }

    static void TrySaveCached(float[] mat, float[] amp) {
        string path = CachePath();
        if (path is null) return;
        try {
            int n = N * N * 4;
            var bytes = new byte[n * 2 * sizeof(float)];
            Buffer.BlockCopy(mat, 0, bytes, 0, n * sizeof(float));
            Buffer.BlockCopy(amp, 0, bytes, n * sizeof(float), n * sizeof(float));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            System.IO.File.WriteAllBytes(tmp, bytes);
            System.IO.File.Move(tmp, path, overwrite: true);
        } catch {
        }
    }

    static void BuildTables(out float[] mat, out float[] amp) {
        mat = new float[N * N * 4];
        amp = new float[N * N * 4];

        for (int j = N - 1; j >= 0; j--) {
            float roughness = j / (float)(N - 1);
            float alpha = MathF.Max(roughness * roughness, 1e-3f);

            for (int i = 0; i < N; i++) {
                float u = i / (float)(N - 1);
                float cosTheta = 1.0f - u * u;
                cosTheta = MathF.Min(MathF.Max(cosTheta, 1e-3f), 1.0f);
                float sinTheta = MathF.Sqrt(1.0f - cosTheta * cosTheta);
                var V = new Vec3(sinTheta, 0f, cosTheta);

                ComputeBrdfMoments(V, alpha, out Vec3 avgDir, out float norm, out float fresnel);

                Vec3 T1, T2;
                if (avgDir.X * avgDir.X + avgDir.Y * avgDir.Y > 1e-8f) {
                    T1 = Vec3.Normalize(new Vec3(avgDir.X, avgDir.Y, 0f));
                } else {
                    T1 = new Vec3(1f, 0f, 0f);
                }
                T2 = new Vec3(-T1.Y, T1.X, 0f);

                float[] p = { alpha, alpha, 0f };
                NelderMead(p, V, alpha, avgDir, T1, T2);
                float m11 = MathF.Max(p[0], 1e-3f), m22 = MathF.Max(p[1], 1e-3f), m13 = p[2];

                Mat3 M = Mat3.Columns(
                    Vec3.Add(Vec3.Scale(T1, m11), Vec3.Scale(avgDir, m13)),
                    Vec3.Scale(T2, m22),
                    avgDir);
                Mat3 inv = Mat3.Inverse(M);
                float s = (MathF.Abs(inv.M22) > 1e-8f) ? 1f / inv.M22 : 1f;
                inv = Mat3.Scale(inv, s);

                int idx = (j * N + i) * 4;
                mat[idx + 0] = inv.M00;
                mat[idx + 1] = inv.M20;
                mat[idx + 2] = inv.M02;
                mat[idx + 3] = inv.M11;

                amp[idx + 0] = norm;
                amp[idx + 1] = fresnel;
                amp[idx + 2] = 0f;
                amp[idx + 3] = 1f;
            }
        }
    }

    static void ComputeBrdfMoments(Vec3 V, float alpha, out Vec3 avgDir, out float norm, out float fresnel) {
        avgDir = new Vec3(0, 0, 0);
        norm = 0f; fresnel = 0f;
        int count = FitSamples * FitSamples;
        for (int a = 0; a < FitSamples; a++) {
            for (int b = 0; b < FitSamples; b++) {
                float u1 = (a + 0.5f) / FitSamples;
                float u2 = (b + 0.5f) / FitSamples;
                float phi = 2f * MathF.PI * u1;
                float cosTheta = MathF.Sqrt((1f - u2) / (1f + (alpha * alpha - 1f) * u2));
                float sinTheta = MathF.Sqrt(MathF.Max(1f - cosTheta * cosTheta, 0f));
                var H = new Vec3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);
                float VoH = Vec3.Dot(V, H);
                var L = Vec3.Sub(Vec3.Scale(H, 2f * VoH), V);
                float NoL = L.Z, NoV = V.Z, NoH = H.Z;
                if (NoL <= 0f || NoV <= 0f) continue;
                VoH = MathF.Max(VoH, 0f);
                float G = SmithG(NoV, NoL, alpha);
                float weight = G * VoH / MathF.Max(NoH * NoV, 1e-6f);
                avgDir = Vec3.Add(avgDir, Vec3.Scale(L, weight));
                norm += weight;
                float fc = MathF.Pow(1f - VoH, 5f);
                fresnel += weight * fc;
            }
        }
        if (norm > 1e-8f) avgDir = Vec3.Scale(avgDir, 1f / norm);
        avgDir = Vec3.Normalize(avgDir);
        norm /= count;
        fresnel /= count;
    }

    static float SmithG(float NoV, float NoL, float alpha) {
        float a2 = alpha * alpha;
        float gv = NoL * MathF.Sqrt(NoV * NoV * (1f - a2) + a2);
        float gl = NoV * MathF.Sqrt(NoL * NoL * (1f - a2) + a2);
        return 0.5f / MathF.Max(gv + gl, 1e-6f) * (2f * NoL * NoV);
    }

    static float FitError(float[] p, Vec3 V, float alpha, Vec3 avgDir, Vec3 T1, Vec3 T2) {
        float m11 = MathF.Max(p[0], 1e-3f), m22 = MathF.Max(p[1], 1e-3f), m13 = p[2];
        Mat3 M = Mat3.Columns(
            Vec3.Add(Vec3.Scale(T1, m11), Vec3.Scale(avgDir, m13)),
            Vec3.Scale(T2, m22),
            avgDir);
        Mat3 inv = Mat3.Inverse(M);
        float err = 0f;
        for (int a = 0; a < FitSamples; a++) {
            for (int b = 0; b < FitSamples; b++) {
                float u1 = (a + 0.5f) / FitSamples;
                float u2 = (b + 0.5f) / FitSamples;
                float phi = 2f * MathF.PI * u1;
                float cosTheta = MathF.Sqrt((1f - u2) / (1f + (alpha * alpha - 1f) * u2));
                float sinTheta = MathF.Sqrt(MathF.Max(1f - cosTheta * cosTheta, 0f));
                var H = new Vec3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);
                float VoH = Vec3.Dot(V, H);
                var L = Vec3.Sub(Vec3.Scale(H, 2f * VoH), V);
                float NoL = L.Z, NoV = V.Z, NoH = H.Z;
                if (NoL <= 0f || NoV <= 0f) continue;
                float pdf = GgxPdf(NoH, VoH, alpha);
                if (pdf <= 1e-8f) continue;
                float G = SmithG(NoV, MathF.Max(NoL, 1e-4f), alpha);
                float brdf = G * MathF.Max(VoH, 0f) / MathF.Max(NoH * NoV, 1e-6f) * pdf;
                Vec3 Lo = Mat3.Mul(inv, L);
                float len = Vec3.Length(Lo);
                if (len < 1e-6f) continue;
                Lo = Vec3.Scale(Lo, 1f / len);
                float jacobian = Mat3.Det(inv) / (len * len * len);
                float ltc = MathF.Max(Lo.Z, 0f) / MathF.PI * MathF.Abs(jacobian);
                float diff = brdf - ltc;
                err += MathF.Abs(diff) * (diff * diff) / MathF.Max(pdf, 1e-6f);
            }
        }
        return err;
    }

    static float GgxPdf(float NoH, float VoH, float alpha) {
        float a2 = alpha * alpha;
        float d = (NoH * NoH * (a2 - 1f) + 1f);
        float D = a2 / MathF.Max(MathF.PI * d * d, 1e-8f);
        return D * NoH / MathF.Max(4f * VoH, 1e-6f);
    }

    static void NelderMead(float[] x, Vec3 V, float alpha, Vec3 avgDir, Vec3 T1, Vec3 T2) {
        const int dim = 3, iters = 40;
        var simplex = new float[dim + 1][];
        var fval = new float[dim + 1];
        for (int s = 0; s <= dim; s++) {
            simplex[s] = (float[])x.Clone();
            if (s > 0) simplex[s][s - 1] += 0.1f * MathF.Max(MathF.Abs(simplex[s][s - 1]), 0.05f) + 0.02f;
            fval[s] = FitError(simplex[s], V, alpha, avgDir, T1, T2);
        }
        for (int it = 0; it < iters; it++) {
            int hi = 0, lo = 0;
            for (int s = 1; s <= dim; s++) { if (fval[s] > fval[hi]) hi = s; if (fval[s] < fval[lo]) lo = s; }
            int hi2 = lo;
            for (int s = 0; s <= dim; s++) if (s != hi && fval[s] > fval[hi2]) hi2 = s;
            var c = new float[dim];
            for (int s = 0; s <= dim; s++) if (s != hi) for (int d = 0; d < dim; d++) c[d] += simplex[s][d];
            for (int d = 0; d < dim; d++) c[d] /= dim;
            var xr = new float[dim];
            for (int d = 0; d < dim; d++) xr[d] = c[d] + 1.0f * (c[d] - simplex[hi][d]);
            float fr = FitError(xr, V, alpha, avgDir, T1, T2);
            if (fr < fval[lo]) {
                var xe = new float[dim];
                for (int d = 0; d < dim; d++) xe[d] = c[d] + 2.0f * (xr[d] - c[d]);
                float fe = FitError(xe, V, alpha, avgDir, T1, T2);
                if (fe < fr) { simplex[hi] = xe; fval[hi] = fe; } else { simplex[hi] = xr; fval[hi] = fr; }
            } else if (fr < fval[hi2]) {
                simplex[hi] = xr; fval[hi] = fr;
            } else {
                var xc = new float[dim];
                for (int d = 0; d < dim; d++) xc[d] = c[d] + 0.5f * (simplex[hi][d] - c[d]);
                float fc = FitError(xc, V, alpha, avgDir, T1, T2);
                if (fc < fval[hi]) { simplex[hi] = xc; fval[hi] = fc; }
                else {
                    for (int s = 0; s <= dim; s++) if (s != lo) {
                        for (int d = 0; d < dim; d++) simplex[s][d] = simplex[lo][d] + 0.5f * (simplex[s][d] - simplex[lo][d]);
                        fval[s] = FitError(simplex[s], V, alpha, avgDir, T1, T2);
                    }
                }
            }
        }
        int best = 0;
        for (int s = 1; s <= dim; s++) if (fval[s] < fval[best]) best = s;
        Array.Copy(simplex[best], x, dim);
    }

    unsafe ID3D12Resource UploadRgba32f(float[] rgba, string name) {
        var desc = ResourceDescription.Texture2D(Format.R32G32B32A32_Float, N, N, arraySize: 1, mipLevels: 1);
        var res = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.CopyDest);
        res.Name = name;

        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1];
        var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(desc, 0, 1, 0, footprints, rowCounts, rowSizes, out ulong total);

        using ID3D12Resource upload = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(total), ResourceStates.GenericRead);

        byte* dst = upload.Map<byte>(0);
        long dstPitch = footprints[0].Footprint.RowPitch;
        long srcPitch = (long)N * 4 * sizeof(float);
        fixed (float* src = rgba) {
            byte* sb = (byte*)src;
            for (int row = 0; row < N; row++)
                Buffer.MemoryCopy(sb + row * srcPitch, dst + (long)footprints[0].Offset + row * dstPitch,
                    srcPitch, srcPitch);
        }
        upload.Unmap(0);

        dev.ExecuteUpload(cl => {
            var d = new TextureCopyLocation(res, 0);
            var s = new TextureCopyLocation(upload, footprints[0]);
            cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            cl.ResourceBarrierTransition(res, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });
        return res;
    }

    int MakeSrv(ID3D12Resource res) {
        int idx = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Format.R32G32B32A32_Float,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(idx));
        return idx;
    }

    public void Dispose() {
        ltc1?.Dispose();
        ltc2?.Dispose();
    }

    readonly struct Vec3 {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 Scale(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static float Length(Vec3 a) => MathF.Sqrt(Dot(a, a));
        public static Vec3 Normalize(Vec3 a) { float l = Length(a); return l > 1e-8f ? Scale(a, 1f / l) : new Vec3(0, 0, 1); }
    }

    readonly struct Mat3 {
        public readonly float M00, M01, M02, M10, M11, M12, M20, M21, M22;
        Mat3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22) {
            M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22;
        }
        public static Mat3 Columns(Vec3 c0, Vec3 c1, Vec3 c2) =>
            new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);
        public static Vec3 Mul(Mat3 m, Vec3 v) => new(
            m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);
        public static float Det(Mat3 m) =>
            m.M00 * (m.M11 * m.M22 - m.M12 * m.M21)
          - m.M01 * (m.M10 * m.M22 - m.M12 * m.M20)
          + m.M02 * (m.M10 * m.M21 - m.M11 * m.M20);
        public static Mat3 Scale(Mat3 m, float s) =>
            new(m.M00 * s, m.M01 * s, m.M02 * s, m.M10 * s, m.M11 * s, m.M12 * s, m.M20 * s, m.M21 * s, m.M22 * s);
        public static Mat3 Inverse(Mat3 m) {
            float det = Det(m);
            float inv = MathF.Abs(det) > 1e-12f ? 1f / det : 0f;
            return new Mat3(
                (m.M11 * m.M22 - m.M12 * m.M21) * inv,
                (m.M02 * m.M21 - m.M01 * m.M22) * inv,
                (m.M01 * m.M12 - m.M02 * m.M11) * inv,
                (m.M12 * m.M20 - m.M10 * m.M22) * inv,
                (m.M00 * m.M22 - m.M02 * m.M20) * inv,
                (m.M02 * m.M10 - m.M00 * m.M12) * inv,
                (m.M10 * m.M21 - m.M11 * m.M20) * inv,
                (m.M01 * m.M20 - m.M00 * m.M21) * inv,
                (m.M00 * m.M11 - m.M01 * m.M10) * inv);
        }
    }
}
