using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using BallisticEngine;          // IStaticMeshRenderer, RuntimeSet

namespace BallisticEngine.DX12;

// Lumen V2 — the SCENE SUBSTRATE for the new GI/reflection stack (plan §"Render Architecture" item 1).
//
// This is NOT a pass and runs NO shading. It owns the durable scene representation every Lumen pass reads:
//   - the shared BLAS/TLAS (Dx12SceneAS) and per-instance bindless geometry/material SRVs (Dx12RtGeometry),
//     both reached through the shared DXR holder (ctx.Dxr) — Lumen does NOT build its own AS, it reuses the
//     one RT shadows/reflections already maintain (stamp-cached: a static scene builds once).
//   - the SURFACE CACHE: P3 realizes the plan's "surface cards" as a PER-TRIANGLE radiance cache. Each scene
//     triangle is one coarse, stable surface record (a card) — they are surface records, NOT camera pixels and
//     NOT world probes. A card-lighting pass writes each triangle's lit first-bounce radiance into CardRadiance
//     (sun + punctual + emissive + sky-visibility), and an RT hit samples CardRadiance[triOffset+prim] instead
//     of re-shading direct light per hit. This is parameterization-free (no UV unwrap), exact for the low-poly
//     GI fixtures, and the place P4 accumulates multi-bounce temporally.
//       Why per-triangle, not a 2D atlas: the engine meshes carry only ONE UV set (no lightmap UV), so an
//       atlas card needs a surface parameterization that does not exist; the triangle IS the finest stable
//       surface record and needs none.
//   - the per-instance META the card-lighting compute reads: {triOffset, bindless geo indices, world 3x4}.
//   - a geometry/material DIRTY stamp; logs object/card/dirty counts.
//
// Gated behind BALLISTIC_DX12_LUMEN: default-off = nothing allocated, byte-identical to a no-Lumen frame.
public sealed class Dx12LumenScene : IDisposable
{
    readonly Dx12Device dev;

    // ---- per-instance meta the card-lighting compute reads (matches HLSL LumenInstanceMeta) ----
    // The bindless geo indices (normal/uv/index/position/triMat) come from Dx12RtGeometry's RtInstance[] (the
    // card-light pass binds that buffer too); THIS meta carries only what RtInstance lacks: the per-instance
    // global triangle offset into CardRadiance + the world matrix (for object→world vertex transform). Same
    // instance order as RtGeometry/SceneAS so one index hits both buffers.
    [StructLayout(LayoutKind.Sequential)]
    struct LumenInstanceMeta
    {
        public uint TriOffset; public uint TriCount; public uint Pad0; public uint Pad1;
        public Matrix4x4 World;   // object→world, transposed on upload (HLSL column-major)
    }

    ID3D12Resource instanceMeta;        // LumenInstanceMeta[] — root SRV, indexed by instance
    public ulong InstanceMetaGpuAddress => instanceMeta?.GPUVirtualAddress ?? 0;
    public int InstanceCount { get; private set; }

    // ---- per-triangle radiance cache (the "cards") ----
    ID3D12Resource cardRadiance;        // float4[totalTris]  (rgb radiance, a unused) — UAV (card-light writes) / SRV (hit reads)
    public ID3D12Resource CardRadiance => cardRadiance;
    public ulong CardRadianceGpuAddress => cardRadiance?.GPUVirtualAddress ?? 0;
    public int TotalTriangles { get; private set; }

    // ---- dirty tracking ----
    int stamp = -1;
    public bool DirtyThisFrame { get; private set; }
    public int DirtyUpdateCount { get; private set; }

    bool loggedThisStamp;

    public Dx12LumenScene(Dx12Device device) { dev = device; }

    public bool Valid => TotalTriangles > 0 && cardRadiance != null;

    // Refresh the substrate for this frame: ensure the shared TLAS + bindless geometry, rebuild the per-instance
    // triangle-range table + the CardRadiance cache on a stamp change, and log the counts. Returns usability.
    public bool Ensure(Dx12FrameContext ctx)
    {
        DirtyThisFrame = false;

        if (!ctx.Dxr.CheckAvailable("Lumen"))
            return false;

        Dx12SceneAS sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid)
            return false;

        ctx.GpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        Dx12RtGeometry rtGeo = ctx.Dxr.RtGeometry;
        rtGeo.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, ctx.GpuDriven);

        int objects = rtGeo.InstanceCount;
        int s = ComputeStamp(sceneAS, objects);
        if (s != stamp || cardRadiance == null)
        {
            stamp = s;
            Rebuild(sceneAS, rtGeo);
            DirtyThisFrame = true;
            DirtyUpdateCount++;
            loggedThisStamp = false;
        }

        if (!loggedThisStamp)
        {
            loggedThisStamp = true;
            string line = $"[Lumen] scene: objects={InstanceCount} cards(tris)={TotalTriangles} " +
                          $"cacheMB={(TotalTriangles * 16L) / (1024 * 1024.0):0.00} dirtyUpdates={DirtyUpdateCount}";
            Console.WriteLine(line);
            Debugging.Log(line);
        }

        return Valid;
    }

    // Build the per-instance triangle-range table (prefix sum of tri counts) + the per-instance world-matrix
    // meta + the CardRadiance cache buffer. The bindless geo indices come from rtGeo's RtInstance[] (the card-
    // light pass reads that buffer); here we only need each instance's tri count (from the mesh) + world matrix.
    unsafe void Rebuild(Dx12SceneAS sceneAS, Dx12RtGeometry rtGeo)
    {
        int n = sceneAS.InstanceCount;
        InstanceCount = n;

        var meta = new LumenInstanceMeta[Math.Max(n, 1)];
        int offset = 0;
        for (int i = 0; i < n; i++)
        {
            int tris = sceneAS.InstanceTriangleCount(i);
            meta[i] = new LumenInstanceMeta
            {
                TriOffset = (uint)offset, TriCount = (uint)tris,
                World = Matrix4x4.Transpose(sceneAS.InstanceWorld(i)),
            };
            offset += tris;
        }
        TotalTriangles = offset;

        instanceMeta?.Dispose();
        instanceMeta = n > 0 ? dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead) : null;

        cardRadiance?.Dispose();
        // float4 per triangle; UAV (card-light writes) readable as a StructuredBuffer SRV by the hit trace.
        int count = Math.Max(TotalTriangles, 1);
        var zero = new Vector4[count];   // start cleared (no stale radiance on a fresh build)
        cardRadiance = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);
        cardRadianceState = ResourceStates.UnorderedAccess;
    }

    public ResourceStates cardRadianceState = ResourceStates.UnorderedAccess;

    int ComputeStamp(Dx12SceneAS sceneAS, int objects)
    {
        var h = new HashCode();
        h.Add(objects);
        for (int i = 0; i < sceneAS.InstanceCount; i++)
        {
            h.Add(sceneAS.InstanceTriangleCount(i));
            Matrix4x4 w = sceneAS.InstanceWorld(i);
            h.Add(w.M41); h.Add(w.M42); h.Add(w.M43);   // translation is enough to detect a moved instance
        }
        return h.ToHashCode();
    }

    public void Dispose()
    {
        instanceMeta?.Dispose(); instanceMeta = null;
        cardRadiance?.Dispose(); cardRadiance = null;
    }
}
