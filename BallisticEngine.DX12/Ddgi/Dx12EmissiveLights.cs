using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12EmissiveLights : IDisposable {
    readonly Dx12Device dev;

    [StructLayout(LayoutKind.Sequential)]
    struct EmissiveTri {
        public Vector4 V0;
        public Vector4 E0;
        public Vector4 E1;
        public Vector4 Radiance;
    }

    public const int MaxLights = 256;

    ID3D12Resource buf;
    int count;
    int stamp = -1;

    public bool Valid => buf != null && count > 0;
    public int Count => count;
    public ulong GpuAddress => buf?.GPUVirtualAddress ?? 0;

    public Dx12EmissiveLights(Dx12Device device) { dev = device; }

    public unsafe void Ensure(IEnumerable<IStaticMeshRenderer> renderers) {
        int s = 17;
        var emitters = new List<(Mesh mesh, Matrix4x4 world, int sm, Vector3 radiance)>();
        foreach (IStaticMeshRenderer r in renderers) {
            Mesh mesh = r.SharedMesh;
            if (mesh == null || mesh.Vertices == null || mesh.Indices == null) continue;
            if (r.SkinningMatrices != null) continue;
            Matrix4x4 world = r.Transform.WorldMatrix;
            int smStart = r.SubMeshIndex >= 0 ? r.SubMeshIndex : 0;
            int smEnd = r.SubMeshIndex >= 0 ? r.SubMeshIndex + 1 : mesh.SubMeshes.Length;
            for (int sm = smStart; sm < smEnd && sm < mesh.SubMeshes.Length; sm++) {
                Material mat = r.MaterialFor(sm);
                if (mat == null) continue;
                if (mat.GetFloat(MaterialSemantic.IsEmissive) < 0.5f) continue;
                Vector4 ec = mat.GetVector(MaterialSemantic.EmissiveColor);
                float ei = mat.GetFloat(MaterialSemantic.EmissiveIntensity);
                Vector3 rad = new Vector3(ec.X, ec.Y, ec.Z) * MathF.Max(ei, 0f);
                if (rad.X + rad.Y + rad.Z <= 1e-4f) continue;
                emitters.Add((mesh, world, sm, rad));
                s = s * 31 + mesh.GetHashCode();
                s = s * 31 + sm;
                s = s * 31 + (int)(rad.X * 13.7f + rad.Y * 7.1f + rad.Z * 3.3f);
                s = s * 31 + world.GetHashCode();
            }
        }
        s = s * 31 + emitters.Count;
        if (s == stamp) return;
        stamp = s;

        var cand = new List<(EmissiveTri tri, float area)>();
        foreach (var (mesh, world, sm, rad) in emitters) {
            SubMeshData sub = mesh.SubMeshes[sm];
            int triStart = sub.IndexStart / 3;
            int triEnd = Math.Min((sub.IndexStart + sub.IndexCount) / 3, mesh.Indices.Length / 3);
            for (int t = triStart; t < triEnd; t++) {
                uint i0 = mesh.Indices[t * 3 + 0], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];
                if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length) continue;
                Vector3 p0 = Vector3.Transform(mesh.Vertices[i0], world);
                Vector3 p1 = Vector3.Transform(mesh.Vertices[i1], world);
                Vector3 p2 = Vector3.Transform(mesh.Vertices[i2], world);
                Vector3 e0 = p1 - p0, e1 = p2 - p0;
                float area = 0.5f * Vector3.Cross(e0, e1).Length();
                if (area <= 1e-7f) continue;
                cand.Add((new EmissiveTri {
                    V0 = new Vector4(p0, 0f), E0 = new Vector4(e0, 0f),
                    E1 = new Vector4(e1, 0f), Radiance = new Vector4(rad, 0f),
                }, area));
            }
        }

        buf?.Dispose(); buf = null; count = 0;
        if (cand.Count == 0) return;

        if (cand.Count > MaxLights) cand.Sort((a, b) => b.area.CompareTo(a.area));
        int n = Math.Min(cand.Count, MaxLights);
        var arr = new EmissiveTri[n];
        for (int i = 0; i < n; i++) arr[i] = cand[i].tri;
        buf = dev.CreateUavBuffer<EmissiveTri>(arr, ResourceStates.GenericRead);
        count = n;
    }

    public void Dispose() { buf?.Dispose(); buf = null; }
}
