using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using BallisticEngine;

namespace BallisticEngine.DX12;

// A6/NEE — world-space EMISSIVE-TRIANGLE light list for next-event estimation at GI hits. DDGI relight rays
// already gather an emissive surface's radiance when a ray HAPPENS to hit it directly + diffusely over multi-
// bounce frames, but that's noisy and slow for an emissive MESH acting as an area luminaire (neon sign, glowing
// panel, light fixture). kajiya (lighting/sample_lights.rgen + inc/lights/triangle.hlsl) instead NEE-samples the
// scene's emissive triangles at every GI hit with a shadow ray → crisp, low-variance contact bounce + proper
// soft area shadows from emitters.
//
// This builds the light list CPU-side from the static whole-mesh geometry (Mesh.Vertices/Indices, world-
// transformed) and the per-submesh emissive material, cached by a (instance-set + emissive-material) stamp — a
// static scene builds ONCE, like the BLAS/TLAS. Budget-capped to MaxLights triangles (the NEE loop is per-GI-hit
// per-probe-ray, so an unbounded list would tank the relight); when a mesh has more emissive triangles than the
// budget, the LARGEST-AREA ones (the most light) are kept. Empty list → NEE is simply off (no emitters), zero cost.
public sealed class Dx12EmissiveLights : IDisposable {
    readonly Dx12Device dev;

    // One emitter triangle in WORLD space. v0/e0/e1 (edge form, matches kajiya's Triangle) + radiance. Packed as
    // float4s for 16-byte cbuffer/structured alignment (w unused). 64 bytes/light.
    [StructLayout(LayoutKind.Sequential)]
    struct EmissiveTri {
        public Vector4 V0;        // xyz = vertex 0 (world), w = unused
        public Vector4 E0;        // xyz = vertex1 - vertex0
        public Vector4 E1;        // xyz = vertex2 - vertex0
        public Vector4 Radiance;  // xyz = emissive radiance (HDR), w = unused
    }

    public const int MaxLights = 256;

    ID3D12Resource buf;
    int count;
    int stamp = -1;

    public bool Valid => buf != null && count > 0;
    public int Count => count;
    public ulong GpuAddress => buf?.GPUVirtualAddress ?? 0;

    public Dx12EmissiveLights(Dx12Device device) { dev = device; }

    // Rebuild the emitter list if the scene geometry/instances or any emissive material changed. The stamp folds
    // the instance count + each renderer's mesh identity + emissive radiance so a moved/edited emitter rebuilds.
    public unsafe void Ensure(IEnumerable<IStaticMeshRenderer> renderers) {
        int s = 17;
        var emitters = new List<(Mesh mesh, Matrix4x4 world, int sm, Vector3 radiance)>();
        foreach (IStaticMeshRenderer r in renderers) {
            Mesh mesh = r.SharedMesh;
            if (mesh == null || mesh.Vertices == null || mesh.Indices == null) continue;
            // SkinningMatrices != null = a skinned mesh; its CPU Vertices are bind-pose, not the animated pose, so
            // its emitters would be in the wrong place. Skip (DDGI is whole-mesh non-skinned anyway).
            if (r.SkinningMatrices != null) continue;
            Matrix4x4 world = r.Transform.WorldMatrix;
            // A split renderer (SubMeshIndex>=0) draws only ITS submesh; a whole-mesh renderer (-1) draws all.
            int smStart = r.SubMeshIndex >= 0 ? r.SubMeshIndex : 0;
            int smEnd = r.SubMeshIndex >= 0 ? r.SubMeshIndex + 1 : mesh.SubMeshes.Length;
            for (int sm = smStart; sm < smEnd && sm < mesh.SubMeshes.Length; sm++) {
                Material mat = r.MaterialFor(sm);
                if (mat == null) continue;
                if (mat.GetFloat(MaterialSemantic.IsEmissive) < 0.5f) continue;
                Vector4 ec = mat.GetVector(MaterialSemantic.EmissiveColor);
                float ei = mat.GetFloat(MaterialSemantic.EmissiveIntensity);
                Vector3 rad = new Vector3(ec.X, ec.Y, ec.Z) * MathF.Max(ei, 0f);
                if (rad.X + rad.Y + rad.Z <= 1e-4f) continue;   // emissive flag but ~black → not a light
                emitters.Add((mesh, world, sm, rad));
                s = s * 31 + mesh.GetHashCode();
                s = s * 31 + sm;
                s = s * 31 + (int)(rad.X * 13.7f + rad.Y * 7.1f + rad.Z * 3.3f);
            }
        }
        s = s * 31 + emitters.Count;
        if (s == stamp && buf != null) return;   // unchanged → keep the cached list
        stamp = s;

        // Collect candidate triangles (world-space) with area, then keep the budget's largest-area ones.
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
                if (area <= 1e-7f) continue;   // degenerate
                cand.Add((new EmissiveTri {
                    V0 = new Vector4(p0, 0f), E0 = new Vector4(e0, 0f),
                    E1 = new Vector4(e1, 0f), Radiance = new Vector4(rad, 0f),
                }, area));
            }
        }

        buf?.Dispose(); buf = null; count = 0;
        if (cand.Count == 0) return;

        // Keep the MaxLights largest-area emitters (most light); a few big panels matter far more than many slivers.
        if (cand.Count > MaxLights) cand.Sort((a, b) => b.area.CompareTo(a.area));
        int n = Math.Min(cand.Count, MaxLights);
        var arr = new EmissiveTri[n];
        for (int i = 0; i < n; i++) arr[i] = cand[i].tri;
        buf = dev.CreateUavBuffer<EmissiveTri>(arr, ResourceStates.GenericRead);
        count = n;
    }

    public void Dispose() { buf?.Dispose(); buf = null; }
}
