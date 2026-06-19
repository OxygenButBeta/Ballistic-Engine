using System;
using System.Collections.Generic;
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
        public uint TriOffset; public uint TriCount; public uint ClusterOffset; public uint ClusterCount;
        public Matrix4x4 World;   // object→world, transposed on upload (HLSL column-major)
    }
    // #2A: TriOffset/ClusterOffset are this instance's bases into the GLOBAL triangle / cluster (record) spaces.
    // A triangle's record = ClusterOffset + TriToCluster[TriOffset + localTri]; the card-light writes / the trace
    // samples RecordRadiance[record]. (TriOffset still indexes the global TriToCluster map below.)

    ID3D12Resource instanceMeta;        // LumenInstanceMeta[] — root SRV, indexed by instance
    public ulong InstanceMetaGpuAddress => instanceMeta?.GPUVirtualAddress ?? 0;
    public int InstanceCount { get; private set; }

    // ---- per-triangle radiance cache (the "cards") — DOUBLE-BUFFERED for P4 temporal accumulation + multi-
    // bounce. Each frame the card-light pass WRITES the "current" buffer while READING the "previous": it EMA-
    // blends the new lit radiance over the previous (conservative temporal stabilization → kills the P2 noise
    // WITHOUT any screen-space history) and gathers a second bounce by sampling the previous cache at its rays'
    // hits (so the cache converges to full multi-bounce GI over a few frames — the Lumen radiance-cache trick).
    // The screen trace reads the "current" (stable) cache. Cards are per-triangle + view-independent, so the
    // temporal EMA needs NO reprojection (a static scene's triangle radiance is stationary). ----
    ID3D12Resource cardRadianceA, cardRadianceB;
    bool writeB;   // which buffer the card-light pass writes THIS frame (ping-pong)
    public ID3D12Resource CardRadianceWrite => writeB ? cardRadianceB : cardRadianceA;
    public ID3D12Resource CardRadianceRead  => writeB ? cardRadianceA : cardRadianceB;   // previous frame's cache
    public ulong CardRadianceWriteGpu => (CardRadianceWrite)?.GPUVirtualAddress ?? 0;
    public ulong CardRadianceReadGpu  => (CardRadianceRead)?.GPUVirtualAddress ?? 0;
    public bool HistoryValid { get; private set; }
    public int TotalTriangles { get; private set; }

    // ---- #2A RadianceCache interface ----
    // A cache RECORD is the unit the radiance cache stores (today = one CLUSTER; the card-light dispatch still
    // walks triangles but writes per-cluster). RecordCount sizes CardRadiance + LastUpdated (the cache shrinks
    // 30-50× vs per-triangle). TriToCluster is the GLOBAL triangle→local-cluster map the card-light + trace read
    // (record = instance.ClusterOffset + TriToCluster[instance.TriOffset + localTri]).
    public int RecordCount { get; private set; }
    ID3D12Resource triToCluster;        // uint[] global tri index → LOCAL cluster index (root SRV)
    public ulong TriToClusterGpuAddress => triToCluster?.GPUVirtualAddress ?? 0;
    // #2A: record (global cluster) index → the cluster's REPRESENTATIVE global triangle index. The card-light
    // pass dispatches ONE thread per record, reads its representative triangle here, and lights+writes that
    // record (no per-triangle race, and the dispatch is RecordCount-wide → cheaper than per-triangle).
    ID3D12Resource clusterToTri;
    public ulong ClusterToTriGpuAddress => clusterToTri?.GPUVirtualAddress ?? 0;

    // ---- P7 #1: per-record "last updated frame" (the update-budget priority input). One uint per triangle
    // (the cache record unit; #2A makes this per-cluster behind the RadianceCache interface). The card-light
    // pass reads it to decide whether a record is "due" this frame and writes the current frame index back when
    // it relights. Persistent + lives as long as the cache (rebuilt/zeroed only on a topology change → a fresh
    // build looks "never updated", so the first budgeted frames sweep the whole scene to fill it). ----
    ID3D12Resource lastUpdated;   // uint[] per record; UAV (card-light writes) + SRV (card-light reads its own age)
    public ulong LastUpdatedGpu => lastUpdated?.GPUVirtualAddress ?? 0;
    ResourceStates lastUpdatedState = ResourceStates.UnorderedAccess;
    public ResourceStates LastUpdatedState => lastUpdatedState;
    public void SetLastUpdatedState(ResourceStates s) => lastUpdatedState = s;
    public ID3D12Resource LastUpdated => lastUpdated;

    // Swap the ping-pong AFTER the card-light pass + trace have consumed this frame's buffers. Called by the
    // pass at the end of Record. The just-written buffer becomes next frame's "previous"/read.
    public void SwapCache() { writeB = !writeB; HistoryValid = true; }

    // ---- dirty tracking ----
    int stamp = -1;
    public bool DirtyThisFrame { get; private set; }
    public int DirtyUpdateCount { get; private set; }

    bool loggedThisStamp;

    public Dx12LumenScene(Dx12Device device) { dev = device; }

    public bool Valid => TotalTriangles > 0 && cardRadianceA != null;

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
        // TOPOLOGY stamp (object count + per-instance tri counts only) — a change means the cache layout is stale
        // → full rebuild + history reset. TRANSFORMS are NOT in this stamp: a moving instance (play-mode physics)
        // must NOT realloc the 100k-triangle cache or reset the temporal EMA every frame; it only needs its
        // world matrix re-uploaded. (Pre-fix the stamp folded translation → CarDemo rebuilt every frame.)
        int s = ComputeTopologyStamp(sceneAS, objects);
        if (s != stamp || cardRadianceA == null)
        {
            stamp = s;
            Rebuild(sceneAS, rtGeo);
            DirtyThisFrame = true;
            DirtyUpdateCount++;
            loggedThisStamp = false;
        }
        else
        {
            // Same topology — just refresh per-instance world matrices in place (cheap; keeps cache + history).
            RefreshTransforms(sceneAS);
        }

        if (!loggedThisStamp)
        {
            loggedThisStamp = true;
            string line = $"[Lumen] scene: objects={InstanceCount} tris={TotalTriangles} records(clusters)={RecordCount} " +
                          $"({(TotalTriangles > 0 ? (float)TotalTriangles / Math.Max(RecordCount, 1) : 0):0.0} tri/cluster) " +
                          $"cacheMB={(RecordCount * 16L) / (1024 * 1024.0):0.00} dirtyUpdates={DirtyUpdateCount}";
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

        // #2A: build the per-instance meta AND the global triangle→cluster map + record (cluster) count in one
        // pass. The cache is sized by RecordCount (clusters), not TotalTriangles — the 30-50× shrink.
        LumenInstanceMeta[] meta = BuildMetaArray(sceneAS, out int total, out int records, out uint[] triCluster, out uint[] clusTri);
        TotalTriangles = total;
        RecordCount = records;

        instanceMeta?.Dispose();
        instanceMeta = n > 0 ? dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead) : null;

        triToCluster?.Dispose();
        triToCluster = total > 0 ? dev.CreateUavBuffer<uint>(triCluster, ResourceStates.GenericRead) : null;

        clusterToTri?.Dispose();
        clusterToTri = records > 0 ? dev.CreateUavBuffer<uint>(clusTri, ResourceStates.GenericRead) : null;

        cardRadianceA?.Dispose(); cardRadianceB?.Dispose();
        // float4 per RECORD (cluster); UAV (card-light writes) readable as a StructuredBuffer SRV by the hit trace.
        int count = Math.Max(RecordCount, 1);
        var zero = new Vector4[count];   // start cleared (no stale radiance on a fresh build)
        cardRadianceA = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);
        cardRadianceB = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);
        cardStateA = cardStateB = ResourceStates.UnorderedAccess;

        // P7 #1: per-record age, zeroed on (re)build. 0 reads as "never updated" → the budgeted card-light pass
        // prioritizes the whole scene over the first frames after a build (full warm-up), then steady-state
        // round-robins. uint.MaxValue would also work as "stale"; 0 keeps the warm-up sweep simplest.
        lastUpdated?.Dispose();
        var zeroAge = new uint[count];
        lastUpdated = dev.CreateUavBuffer<uint>(zeroAge, ResourceStates.UnorderedAccess);
        lastUpdatedState = ResourceStates.UnorderedAccess;

        writeB = false;
        HistoryValid = false;   // a rebuilt cache has no valid history → the EMA starts fresh (alpha=1 first frame)
    }

    // Resource-state tracking per buffer (the pass transitions UAV↔non-pixel-SRV around the card-light dispatch
    // + trace read). Exposed so the pass can manage barriers on whichever buffer is read/written this frame.
    public ResourceStates cardStateA = ResourceStates.UnorderedAccess, cardStateB = ResourceStates.UnorderedAccess;
    public ResourceStates StateOf(ID3D12Resource r) => r == cardRadianceB ? cardStateB : cardStateA;
    public void SetState(ID3D12Resource r, ResourceStates s) { if (r == cardRadianceB) cardStateB = s; else cardStateA = s; }

    // TOPOLOGY-only stamp: object count + per-instance triangle counts. Deliberately EXCLUDES transforms (a
    // moving instance keeps the same layout → no rebuild, just RefreshTransforms). A mesh/instance add/remove
    // changes the counts → rebuild + history reset.
    int ComputeTopologyStamp(Dx12SceneAS sceneAS, int objects)
    {
        var h = new HashCode();
        h.Add(objects);
        for (int i = 0; i < sceneAS.InstanceCount; i++)
            h.Add(sceneAS.InstanceTriangleCount(i));
        return h.ToHashCode();
    }

    // Re-upload only the per-instance world matrices (topology unchanged). Cheap (a handful of instances) and
    // keeps the big CardRadiance cache + its temporal history intact across instance motion.
    unsafe void RefreshTransforms(Dx12SceneAS sceneAS)
    {
        if (sceneAS.InstanceCount == 0) return;
        // Re-cluster is a cached per-mesh no-op (topology unchanged), so this just rebuilds the meta with the same
        // cluster offsets + re-uploads world matrices. The triToCluster map is unchanged → not re-uploaded.
        LumenInstanceMeta[] meta = BuildMetaArray(sceneAS, out _, out _, out _, out _);
        instanceMeta?.Dispose();
        instanceMeta = dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead);
    }

    // Per-instance {triOffset, triCount, clusterOffset, clusterCount, world} + the GLOBAL triangle→local-cluster
    // map + the record→global-representative-tri map + the total record (cluster) count. Shared by Rebuild
    // (topology change) and RefreshTransforms (motion only — clustering is mesh-cached so re-calling is cheap).
    LumenInstanceMeta[] BuildMetaArray(Dx12SceneAS sceneAS, out int total, out int records, out uint[] triCluster, out uint[] clusterTri)
    {
        int n = sceneAS.InstanceCount;
        var meta = new LumenInstanceMeta[Math.Max(n, 1)];
        int totalTris = 0;
        for (int i = 0; i < n; i++) totalTris += sceneAS.InstanceTriangleCount(i);
        triCluster = new uint[Math.Max(totalTris, 1)];
        var clusterTriList = new List<uint>(Math.Max(totalTris / 64, 16));   // record → global representative tri

        int offset = 0, clusterOffset = 0;
        for (int i = 0; i < n; i++)
        {
            int tris = sceneAS.InstanceTriangleCount(i);
            var mc = Dx12LumenCluster.Cluster(sceneAS.InstanceMesh(i));
            int copyN = Math.Min(tris, mc.TriToCluster.Length);
            for (int t = 0; t < copyN; t++) triCluster[offset + t] = (uint)mc.TriToCluster[t];
            // Append this instance's cluster representatives in LOCAL-cluster order — that matches the global
            // record index (clusterOffset + localCluster), so clusterTriList[record] is the representative.
            for (int c = 0; c < mc.ClusterFirstTri.Length; c++)
                clusterTriList.Add((uint)(offset + mc.ClusterFirstTri[c]));   // global representative tri index

            meta[i] = new LumenInstanceMeta
            {
                TriOffset = (uint)offset, TriCount = (uint)tris,
                ClusterOffset = (uint)clusterOffset, ClusterCount = (uint)mc.ClusterCount,
                World = Matrix4x4.Transpose(sceneAS.InstanceWorld(i)),
            };
            offset += tris;
            clusterOffset += mc.ClusterCount;
        }
        total = offset;
        records = clusterOffset;
        clusterTri = clusterTriList.Count > 0 ? clusterTriList.ToArray() : new uint[1];
        return meta;
    }

    public void Dispose()
    {
        instanceMeta?.Dispose(); instanceMeta = null;
        triToCluster?.Dispose(); triToCluster = null;
        clusterToTri?.Dispose(); clusterToTri = null;
        cardRadianceA?.Dispose(); cardRadianceA = null;
        cardRadianceB?.Dispose(); cardRadianceB = null;
        lastUpdated?.Dispose(); lastUpdated = null;
    }
}
