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

    // ---- Sıra 5: MESH-CARD planar frames (WORLD-space, record-indexed) + texel-grid radiance cache ----
    // Each record (cluster) carries a world-space card plane (Dx12LumenCluster.ClusterCard transformed per
    // instance). A hit at world point P maps to card UV → a TEXEL within the record's TexelDim×TexelDim tile, so
    // the cache stores per-texel radiance (cluster-interior detail) instead of one value per record. Gated behind
    // BALLISTIC_DX12_LUMEN_MESHCARDS; when off, only the legacy 1-value-per-record path allocates (byte-identical).
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuClusterCard   // 4× float4 = 64 B; matches HLSL ClusterCard
    {
        public Vector3 Origin; public float InvExtentU;
        public Vector3 U;      public float InvExtentV;
        public Vector3 V;      public float Pad0;
        public Vector3 Normal; public float Pad1;
    }
    ID3D12Resource clusterCards;   // GpuClusterCard[] per record (root SRV); built only when mesh-cards armed
    public ulong ClusterCardsGpuAddress => clusterCards?.GPUVirtualAddress ?? 0;
    // Texel grid edge per card (TexelDim²  texels/record). Default 1 = legacy single-value record (byte-identical
    // off). Mesh-cards arm it to e.g. 4 → 16 texels/record. Env BALLISTIC_DX12_LUMEN_MESHCARD_DIM overrides.
    public int TexelDim { get; private set; } = 1;
    public int TexelsPerRecord => TexelDim * TexelDim;

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

        bool prof = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_PROFILE") == "1";
        var sw = prof ? System.Diagnostics.Stopwatch.StartNew() : null;
        void P(string t) { if (prof) { sw.Stop(); Console.WriteLine($"[SceneProf] {t} {sw.Elapsed.TotalMilliseconds:0.00}ms"); sw.Restart(); } }

        Dx12SceneAS sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid)
            return false;
        P("sceneAS.Ensure");

        ctx.GpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        P("EnsureMaterialTable");
        Dx12RtGeometry rtGeo = ctx.Dxr.RtGeometry;
        rtGeo.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, ctx.GpuDriven);
        P("rtGeo.Ensure");

        int objects = rtGeo.InstanceCount;
        // TOPOLOGY stamp (object count + per-instance tri counts only) — a change means the cache layout is stale
        // → full rebuild + history reset. TRANSFORMS are NOT in this stamp: a moving instance (play-mode physics)
        // must NOT realloc the 100k-triangle cache or reset the temporal EMA every frame; it only needs its
        // world matrix re-uploaded. (Pre-fix the stamp folded translation → CarDemo rebuilt every frame.)
        int s = ComputeTopologyStamp(sceneAS, objects);
        bool willRecreate = s != stamp || cardRadianceA == null
                            || (transformStamp != ComputeTransformStamp(sceneAS));
        // P0b: a Rebuild/RefreshTransforms below recreates GPU buffers MID-FRAME (CreateUavBuffer → its own
        // upload submit) that the PREVIOUS frame, still in flight under overlap, may be reading. Drain that
        // frame FIRST so the realloc can't race it (the realloc'd buffers are then read by nobody). Only on a
        // recreate frame (topology change / instance motion) — steady-state frames skip it and overlap fully.
        // No-op when overlap is off (LastFrameFenceTarget already reached). RequestFrameSync (below) then keeps
        // the NEXT frame from overlapping into this recreate too.
        if (willRecreate) dev.WaitForFrame(dev.LastFrameFenceTarget);
        if (s != stamp || cardRadianceA == null)
        {
            stamp = s;
            Rebuild(sceneAS, rtGeo);
            DirtyThisFrame = true;
            DirtyUpdateCount++;
            loggedThisStamp = false;
            // P0b: Rebuild recreated GPU buffers that aren't N-buffered (the cache layout is topology-keyed, not
            // frame-keyed). Force this frame to drain before the next records, so frame N+1 can't read/recycle
            // across the realloc under overlap. Steady-state (no rebuild) frames overlap fully. No-op overlap-off.
            dev.RequestFrameSync();
        }
        else
        {
            // Same topology — refresh per-instance world matrices ONLY if a transform actually changed. PERF: this
            // was the entire Lumen CPU cost (~17ms on Bistro exterior) — it ran EVERY frame, rebuilding the
            // instance-meta + world-space card buffers and re-uploading them even on a totally STATIC scene. A
            // transform stamp skips it when nothing moved → 0ms on a static scene, byte-identical output.
            int ts = ComputeTransformStamp(sceneAS);
            if (ts != transformStamp)
            {
                transformStamp = ts;
                RefreshTransforms(sceneAS);
                dev.RequestFrameSync();   // P0b: same as Rebuild — recreated non-N-buffered buffers this frame.
            }
        }
        P(s != stamp ? "Rebuild" : "RefreshTransforms");

        if (!loggedThisStamp)
        {
            loggedThisStamp = true;
            string line = $"[Lumen] scene: objects={InstanceCount} tris={TotalTriangles} records(clusters)={RecordCount} " +
                          $"({(TotalTriangles > 0 ? (float)TotalTriangles / Math.Max(RecordCount, 1) : 0):0.0} tri/cluster) " +
                          $"meshCards={(TexelDim > 1 ? $"ON({TexelDim}x{TexelDim}={TexelsPerRecord} texels/rec)" : "off")} " +
                          $"cacheMB={(RecordCount * (long)TexelsPerRecord * 16L) / (1024 * 1024.0):0.00} dirtyUpdates={DirtyUpdateCount}";
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
        LumenInstanceMeta[] meta = BuildMetaArray(sceneAS, out int total, out int records, out uint[] triCluster, out uint[] clusTri, out GpuClusterCard[] cards);
        TotalTriangles = total;
        RecordCount = records;

        // Sıra 5: mesh-card texel grid. Armed by BALLISTIC_DX12_LUMEN_MESHCARDS=1 → TexelDim N (default 4, 16
        // texels/record); off → TexelDim 1 (legacy single-value record, cache byte-identical to pre-Sıra-5). The
        // cache + age buffers are sized RecordCount × TexelsPerRecord.
        bool meshCards = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_MESHCARDS") == "1";
        TexelDim = meshCards ? Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_MESHCARD_DIM", 4f), 1, 8) : 1;

        // P0b: defer release of the OLD buffers (the GPU may still read them for the in-flight frame under overlap).
        dev.DeferredRelease(instanceMeta);
        instanceMeta = n > 0 ? dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead) : null;

        dev.DeferredRelease(triToCluster);
        triToCluster = total > 0 ? dev.CreateUavBuffer<uint>(triCluster, ResourceStates.GenericRead) : null;

        dev.DeferredRelease(clusterToTri);
        clusterToTri = records > 0 ? dev.CreateUavBuffer<uint>(clusTri, ResourceStates.GenericRead) : null;

        dev.DeferredRelease(clusterCards);
        clusterCards = records > 0 ? dev.CreateUavBuffer<GpuClusterCard>(cards, ResourceStates.GenericRead) : null;

        dev.DeferredRelease(cardRadianceA); dev.DeferredRelease(cardRadianceB);
        // float4 per TEXEL (TexelsPerRecord per record); UAV (card-light writes) readable as a StructuredBuffer SRV
        // by the hit trace. count = RecordCount × TexelsPerRecord (== RecordCount when TexelDim 1, byte-identical).
        int count = Math.Max(RecordCount * TexelsPerRecord, 1);
        var zero = new Vector4[count];   // start cleared (no stale radiance on a fresh build)
        cardRadianceA = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);
        cardRadianceB = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);
        cardStateA = cardStateB = ResourceStates.UnorderedAccess;

        // P7 #1: per-record age, zeroed on (re)build. 0 reads as "never updated" → the budgeted card-light pass
        // prioritizes the whole scene over the first frames after a build (full warm-up), then steady-state
        // round-robins. uint.MaxValue would also work as "stale"; 0 keeps the warm-up sweep simplest.
        // P7 #1 age stays PER-RECORD (one update decision per cluster, not per texel — the whole card relights as a
        // unit), so it is sized RecordCount, NOT count.
        dev.DeferredRelease(lastUpdated);
        var zeroAge = new uint[Math.Max(RecordCount, 1)];
        lastUpdated = dev.CreateUavBuffer<uint>(zeroAge, ResourceStates.UnorderedAccess);
        lastUpdatedState = ResourceStates.UnorderedAccess;

        writeB = false;
        HistoryValid = false;   // a rebuilt cache has no valid history → the EMA starts fresh (alpha=1 first frame)
        // Rebuild already uploaded THIS frame's instance transforms (BuildMetaArray) — record their stamp so the
        // NEXT frame doesn't fire a redundant RefreshTransforms (which re-uploads the identical matrices). Pre-fix
        // transformStamp stayed -1 after a rebuild → the following frame always ran RefreshTransforms once, an
        // unnecessary per-buffer realloc (and, under frame overlap, a mid-frame GPU-resource recreate hazard).
        transformStamp = ComputeTransformStamp(sceneAS);
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

    // PERF: a cheap stamp of all instance WORLD matrices — RefreshTransforms (the ~17ms/frame cost) runs only when
    // this changes (an instance actually moved). On a static scene it never changes → RefreshTransforms is skipped
    // entirely. Cheap: a handful of instances on the whole-mesh path (Bistro = 1 instance).
    int transformStamp = -1;
    int ComputeTransformStamp(Dx12SceneAS sceneAS)
    {
        var h = new HashCode();
        for (int i = 0; i < sceneAS.InstanceCount; i++)
        {
            // FULL 3×3 rotation/scale block + translation. The old stamp hashed only the diagonal (M11/M22/M33) +
            // translation, so a PURE ROTATION — which lives entirely in the off-diagonal terms (M12/M13/M21/…) —
            // left the stamp unchanged: RefreshTransforms was skipped and the world-space card frames (ClusterCards)
            // kept the stale orientation, so the GI cache lit a rotated instance as if it never turned. Hashing the
            // whole upper-left 3×3 catches rotation, shear, and non-uniform scale. Cost is negligible (a handful of
            // instances on the whole-mesh path; Bistro = 1).
            Matrix4x4 w = sceneAS.InstanceWorld(i);
            h.Add(w.M11); h.Add(w.M12); h.Add(w.M13);
            h.Add(w.M21); h.Add(w.M22); h.Add(w.M23);
            h.Add(w.M31); h.Add(w.M32); h.Add(w.M33);
            h.Add(w.M41); h.Add(w.M42); h.Add(w.M43);   // translation
        }
        return h.ToHashCode();
    }

    // Re-upload only the per-instance world matrices (topology unchanged). Cheap (a handful of instances) and
    // keeps the big CardRadiance cache + its temporal history intact across instance motion.
    unsafe void RefreshTransforms(Dx12SceneAS sceneAS)
    {
        if (sceneAS.InstanceCount == 0) return;
        // Re-cluster is a cached per-mesh no-op (topology unchanged), so this just rebuilds the meta with the same
        // cluster offsets + re-uploads world matrices. The triToCluster map is unchanged → not re-uploaded. Sıra 5:
        // the card frames ARE world-space, so a moved instance needs them re-uploaded too (rebuilt by BuildMetaArray).
        // PP3: pass needTriMaps=false so the per-triangle triCluster/clusterTri maps (topology-invariant, and thrown
        // away here as `out _`) are NOT re-derived every moving frame — the big CPU loop over totalTris is skipped.
        // BALLISTIC_DX12_LUMEN_PARTIAL_REFRESH=0 reverts to the full rebuild (needTriMaps=true) for A/B.
        bool partial = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_PARTIAL_REFRESH") != "0";
        LumenInstanceMeta[] meta = BuildMetaArray(sceneAS, out _, out _, out _, out _, out GpuClusterCard[] cards,
                                                  needTriMaps: !partial);
        // P0b: DEFER the old buffers' release — the GPU may still read them for the frame in flight under overlap
        // (immediate Dispose = use-after-free → device removal). Freed once the GPU passes the in-flight frame.
        dev.DeferredRelease(instanceMeta);
        instanceMeta = dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead);
        if (clusterCards != null)
        {
            dev.DeferredRelease(clusterCards);
            clusterCards = dev.CreateUavBuffer<GpuClusterCard>(cards, ResourceStates.GenericRead);
        }
    }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    // Per-instance {triOffset, triCount, clusterOffset, clusterCount, world} + the GLOBAL triangle→local-cluster
    // map + the record→global-representative-tri map + the total record (cluster) count. Shared by Rebuild
    // (topology change) and RefreshTransforms (motion only — clustering is mesh-cached so re-calling is cheap).
    // PP3: `needTriMaps` controls whether the topology-only triangle→cluster / cluster→tri maps are (re)built.
    // Rebuild (topology change) passes true. RefreshTransforms (motion only — same topology) passes false: those
    // two maps are index-into-mesh, INVARIANT under instance motion, so re-deriving them every moving frame is
    // pure waste (RefreshTransforms threw them away as `out _` anyway). On false they come back as empty 1-element
    // arrays (callers ignore them) and the per-triangle copy loop + totalTris allocation are skipped entirely.
    // Only the per-instance world meta + the world-space card frames (which DO depend on the matrix) are rebuilt.
    LumenInstanceMeta[] BuildMetaArray(Dx12SceneAS sceneAS, out int total, out int records, out uint[] triCluster,
                                       out uint[] clusterTri, out GpuClusterCard[] cards, bool needTriMaps = true)
    {
        int n = sceneAS.InstanceCount;
        var meta = new LumenInstanceMeta[Math.Max(n, 1)];
        int totalTris = 0;
        for (int i = 0; i < n; i++) totalTris += sceneAS.InstanceTriangleCount(i);
        triCluster = needTriMaps ? new uint[Math.Max(totalTris, 1)] : new uint[1];
        var clusterTriList = needTriMaps ? new List<uint>(Math.Max(totalTris / 64, 16)) : null;   // record → global representative tri
        var cardList = new List<GpuClusterCard>(Math.Max(totalTris / 64, 16));   // record → WORLD-space card frame

        int offset = 0, clusterOffset = 0;
        for (int i = 0; i < n; i++)
        {
            int tris = sceneAS.InstanceTriangleCount(i);
            var mc = Dx12LumenCluster.Cluster(sceneAS.InstanceMesh(i));
            // PP3: the triangle→cluster + cluster→tri maps are topology-only (no world matrix) — skip when the
            // caller (RefreshTransforms) doesn't need them. The clustering itself (Dx12LumenCluster.Cluster) is
            // mesh-cached either way, so this only skips the per-triangle copy + the representative-list append.
            if (needTriMaps)
            {
                int copyN = Math.Min(tris, mc.TriToCluster.Length);
                for (int t = 0; t < copyN; t++) triCluster[offset + t] = (uint)mc.TriToCluster[t];
                // Append this instance's cluster representatives in LOCAL-cluster order — that matches the global
                // record index (clusterOffset + localCluster), so clusterTriList[record] is the representative.
                for (int c = 0; c < mc.ClusterFirstTri.Length; c++)
                    clusterTriList.Add((uint)(offset + mc.ClusterFirstTri[c]));   // global representative tri index
            }

            // Sıra 5: transform each cluster's OBJECT-space card frame into WORLD space for THIS instance, in the
            // SAME local-cluster order → record-indexed. Origin = point (w=1); U/V/Normal = directions (w=0). The
            // span scales with the world matrix, so InvExtent is divided by the axis' world length.
            Matrix4x4 w = sceneAS.InstanceWorld(i);
            for (int c = 0; c < mc.Cards.Length; c++)
            {
                var card = mc.Cards[c];
                Vector3 wo = Vector3.Transform(card.Origin, w);
                Vector3 wu = Vector3.TransformNormal(card.U, w);
                Vector3 wv = Vector3.TransformNormal(card.V, w);
                Vector3 wn = Vector3.TransformNormal(card.Normal, w);
                float ulen = wu.Length(); float vlen = wv.Length();
                cardList.Add(new GpuClusterCard
                {
                    Origin = wo, InvExtentU = card.InvExtentU / MathF.Max(ulen, 1e-6f),
                    U = ulen > 1e-6f ? wu / ulen : Vector3.UnitX, InvExtentV = card.InvExtentV / MathF.Max(vlen, 1e-6f),
                    V = vlen > 1e-6f ? wv / vlen : Vector3.UnitZ, Pad0 = 0,
                    Normal = wn.LengthSquared() > 1e-12f ? Vector3.Normalize(wn) : Vector3.UnitY, Pad1 = 0,
                });
            }

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
        clusterTri = (clusterTriList is { Count: > 0 }) ? clusterTriList.ToArray() : new uint[1];
        cards = cardList.Count > 0 ? cardList.ToArray() : new GpuClusterCard[1];
        return meta;
    }

    public void Dispose()
    {
        instanceMeta?.Dispose(); instanceMeta = null;
        triToCluster?.Dispose(); triToCluster = null;
        clusterToTri?.Dispose(); clusterToTri = null;
        clusterCards?.Dispose(); clusterCards = null;
        cardRadianceA?.Dispose(); cardRadianceA = null;
        cardRadianceB?.Dispose(); cardRadianceB = null;
        lastUpdated?.Dispose(); lastUpdated = null;
    }
}
