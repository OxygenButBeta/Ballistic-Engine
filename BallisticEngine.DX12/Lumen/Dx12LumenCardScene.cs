using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;               // DxcShaderStage
using PrimitiveTopology = Vortice.Direct3D.PrimitiveTopology;
using Vortice.DXGI;
using BallisticEngine;          // Mesh, MeshCard, MeshCards, Material, MaterialSemantic, IStaticMeshRenderer

namespace BallisticEngine.DX12;

// Lumen FAZ 3b — the runtime CARD SCENE + SURFACE-CACHE ATLAS substrate.
//
// Gathers every visible mesh's OFFLINE per-mesh cards (Mesh.Cards, MeshCards — built at import in FAZ 3a) into a
// flat WORLD-space GPU card list and a PHYSICAL surface-cache atlas with a VIRTUAL page table, the way UE5's
// FLumenSceneData / surface-cache pages do. This is the substrate the capture (3c) rasterizes into and the
// lighting/trace (3d/5+) read from. FAZ 3b builds NOTHING lit — it only places the cards + allocates atlas pages,
// and ends with a DEBUG VIEW (Dx12LumenGiPass) that ray-tests the placed card OBBs so correctness is provable.
//
// Structure (v1 — "allocate all" residency, but kept paging-ready):
//   - WORLD-space card list (GpuLumenCard[], root SRV): each mesh-local MeshCard transformed per instance the way
//     Aurora transforms its cluster cards (Transform Origin, TransformNormal axes + normalize, extents scaled by
//     the transformed-axis world length). Each card carries the page-table index it was allocated (0xFFFFFFFF =
//     unallocated/dropped).
//   - PAGE TABLE (GpuLumenPage[], root SRV): one entry per allocated card. Records the card's physical-atlas texel
//     rect (offset + size) + the card id + its chosen resolution level. This is a REAL structure (not a hardcoded
//     non-paged layout) so 3c+ can swap to demand paging without reshaping the data — for v1 every fitting card
//     gets a resident page up front.
//   - PHYSICAL ATLAS (persistent 2D textures, NOT pooled — cross-frame cache): Albedo/Normal/Emissive/Depth +
//     DirectLighting/FinalLighting, all AllowRenderTarget|AllowUnorderedAccess (3c rasterizes, 3d/trace read).
//     Sized as a page grid (default 2048² = 16×16 pages of PhysicalPageSize). SRV+UAV for each live in the RESERVED
//     Dx12BindlessTail.LumenSurfaceCacheTableBase block (persistent, written once — see the tail note).
//
// Allocator: a simple SHELF/ROW packer over the physical atlas (advance X within a row of height = the tallest page
// placed in that row, wrap to the next row at the atlas width). If the atlas fills, the overflow cards are DROPPED
// with PageId = 0xFFFFFFFF and the count is LOGGED — never silently truncated.
//
// Dirty handling mirrors Dx12LumenScene: cards are rebuilt on a TOPOLOGY change (object/card-count change), and a
// TRANSFORM change re-derives the world cards + re-uploads only the card buffer (the page allocation is topology-
// invariant — moving an instance doesn't change its atlas footprint).
//
// Gated: nothing here is constructed unless armed (BALLISTIC_DX12_LUMEN_CARDS=1 OR Lumen GI on).
public sealed class Dx12LumenCardScene : IDisposable {
    readonly Dx12Device dev;

    // ---- UE Lumen.h constants (verbatim) ----
    const int PhysicalPageSize = 128;   // texels per physical atlas page edge
    const int MinResLevel = 3;          // 2^3 = 8 texels (smallest card footprint)
    const int MaxResLevel = 11;         // 2^11 = 2048 texels (UE max; v1 clamps far below — see CapResLevel)
    const int CardTileSize = 8;         // texels per card tile (capture granularity)
    // v1 caps the per-card resolution so a card never exceeds one physical page (keeps the shelf allocator simple +
    // residency cheap). 7 → 128 texels = exactly PhysicalPageSize.
    const int CapResLevel = 7;

    // ---- GPU structs (16-byte aligned) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuLumenCard {   // 64 B, world-space
        public Vector3 Origin; public uint PageId;     // PageId = index into the page table (0xFFFFFFFF = unallocated)
        public Vector3 AxisX;  public float ExtentX;   // unit axis + world half-size (capture-plane U)
        public Vector3 AxisY;  public float ExtentY;   // (capture-plane V)
        public Vector3 AxisZ;  public float ExtentZ;   // outward view normal + world depth half-size
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GpuLumenPage {   // 32 B
        public uint AtlasOffsetX, AtlasOffsetY;        // texel offset of this page's rect in the physical atlas
        public uint SizeX, SizeY;                      // texel size of the card's footprint (<= PhysicalPageSize)
        public uint CardId; public uint ResLevel; public uint Pad0, Pad1;
    }

    // ---- card list + page table (root SRVs) ----
    ID3D12Resource cardBuf;      // GpuLumenCard[]
    ID3D12Resource pageBuf;      // GpuLumenPage[]
    public ulong CardBufferGpuAddress => cardBuf?.GPUVirtualAddress ?? 0;
    public ulong PageBufferGpuAddress => pageBuf?.GPUVirtualAddress ?? 0;
    public int CardCount { get; private set; }
    public int PageCount { get; private set; }
    public int DroppedCards { get; private set; }

    // Per-instance card range (offset into the card list + count) — Dx12LumenScene writes these into its meta.
    public struct InstanceCardRange { public uint Offset; public uint Count; }
    InstanceCardRange[] instanceRanges = Array.Empty<InstanceCardRange>();
    public InstanceCardRange RangeFor(int instance) =>
        (uint)instance < (uint)instanceRanges.Length ? instanceRanges[instance] : default;

    // ---- physical atlas (persistent, cross-frame) ----
    public int AtlasSize { get; private set; } = 2048;       // texels per atlas edge (square)
    public int PageGridDim => AtlasSize / PhysicalPageSize;   // pages per atlas edge (2048/128 = 16)

    sealed class Atlas {
        public ID3D12Resource Tex;
        public int SrvBindless, UavBindless;
        public CpuDescriptorHandle SrvCpu, UavCpu;
        public Format Fmt;
    }
    Atlas albedo, normal, emissive, depthA, directLight, finalLight;
    // FAZ 3d — SECOND FinalLighting atlas. The multi-bounce reads LAST frame's lit cache (finalLightRead) while
    // writing THIS frame's (finalLightWrite); they ping-pong via SwapFinalLighting() after each lighting pass.
    Atlas finalLightB;
    Atlas finalLightRead, finalLightWrite;   // alias one of {finalLight, finalLightB} each frame
    // false until the first lit frame — the first frame's multi-bounce reads nothing (black) then accumulates.
    bool finalValid;
    public bool FinalValid => finalValid;

    // FAZ 3c — capture RTVs. One RTV per CAPTURED attribute atlas (Albedo/Normal/Emissive/Depth — DirectLighting/
    // FinalLighting are written by 3d, no capture RTV). Plus a transient PhysicalPageSize² D32 depth target reused
    // per card for the ortho-rasterize z-test. CPU, non-shader-visible heaps (RTV/DSV descriptors). Built in EnsureAtlas.
    ID3D12DescriptorHeap captureRtvHeap;   // 4 RTVs: albedo, normal, emissive, depth (this fixed order)
    uint captureRtvInc;
    ID3D12Resource captureDepth;           // full-atlas D32 (shares the RTV coord space; cleared per-page rect)
    ID3D12DescriptorHeap captureDsvHeap;
    CpuDescriptorHandle captureDsvHandle;

    // FAZ 3c — capture PSO (CBV b0 + 6-SRV material table t0-t5 + static sampler s0) + a per-draw CB ring + a
    // shader-visible material SRV heap. Built once (lazily) on the first capture.
    ID3D12RootSignature captureRootSig;
    ID3D12PipelineState capturePso;
    InputLayoutDescription captureLayout;
    ID3D12Resource captureCbRing;          // per-draw LumenCaptureConstants, upload heap (mapped)
    unsafe byte* captureCbMapped;
    int captureCbSlotSize, captureCbSlotCount;
    Dx12DescriptorHeap captureSrv;         // shader-visible material SRV table (6 per draw)
    bool captureBuilt;

    [StructLayout(LayoutKind.Sequential)]
    struct LumenCaptureConstants {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Vector3 CardAxisX; public float Pad0;
        public Vector3 CardAxisY; public float Pad1;
        public Vector3 CardAxisZ; public float CardExtentZ;
        public Vector3 CardOrigin; public float Pad2;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor; public float HasEmissive;
        public float Metallic, Roughness, NormalStrength, NormalFlipY;
        public float HasMetallicMap, HasRoughnessMap, PackedOrm, Cutout;
    }
    const int CaptureMaterialSrvCount = 6;

    public ID3D12Resource AlbedoAtlas => albedo?.Tex;
    public ID3D12Resource NormalAtlas => normal?.Tex;
    public ID3D12Resource EmissiveAtlas => emissive?.Tex;
    public ID3D12Resource DepthAtlas => depthA?.Tex;
    public ID3D12Resource DirectLightingAtlas => directLight?.Tex;
    public ID3D12Resource FinalLightingAtlas => finalLight?.Tex;
    public int AlbedoSrvBindless => albedo?.SrvBindless ?? -1;
    public int AlbedoUavBindless => albedo?.UavBindless ?? -1;
    public int NormalSrvBindless => normal?.SrvBindless ?? -1;
    public int EmissiveSrvBindless => emissive?.SrvBindless ?? -1;
    public int DepthSrvBindless => depthA?.SrvBindless ?? -1;
    public int FinalLightingSrvBindless => finalLightRead?.SrvBindless ?? finalLight?.SrvBindless ?? -1;

    // FAZ 3d — bindless reserved-tail indices the lighting compute reads via ResourceDescriptorHeap[].
    public int AlbedoSrvIdx => albedo?.SrvBindless ?? -1;
    public int NormalSrvIdx => normal?.SrvBindless ?? -1;
    public int EmissiveSrvIdx => emissive?.SrvBindless ?? -1;
    public int DepthSrvIdx => depthA?.SrvBindless ?? -1;
    public int DirectUavIdx => directLight?.UavBindless ?? -1;
    public int FinalReadSrvIdx => finalLightRead?.SrvBindless ?? -1;
    public int FinalWriteUavIdx => finalLightWrite?.UavBindless ?? -1;

    // FAZ 3d — CPU SRV handles for the debug blit (DirectLighting + the current READ FinalLighting = last lit frame).
    public CpuDescriptorHandle DirectSrvCpu => directLight?.SrvCpu ?? default;
    public CpuDescriptorHandle FinalSrvCpu  => finalLightRead?.SrvCpu ?? finalLight?.SrvCpu ?? default;

    // FAZ 3c — CPU SRV handles for the captured atlases (debug blit copies these into a shader-visible heap, the way
    // RecordSdfDebug copies the clipmap SRV). Albedo/Normal/Emissive/Depth, by capture-attribute name.
    public CpuDescriptorHandle AlbedoSrvCpu   => albedo?.SrvCpu   ?? default;
    public CpuDescriptorHandle NormalSrvCpu   => normal?.SrvCpu   ?? default;
    public CpuDescriptorHandle EmissiveSrvCpu => emissive?.SrvCpu ?? default;
    public CpuDescriptorHandle DepthSrvCpu    => depthA?.SrvCpu   ?? default;

    // FAZ 3c — true on a frame the capture pass actually ran (cards (re)built since the last capture). The debug blit
    // reads it only to log; the atlas content persists across frames regardless.
    public bool Captured { get; private set; }
    bool atlasBuilt;

    public bool Valid => CardCount > 0 && cardBuf != null;

    // ====================================================================================================
    // FAZ 3d — SURFACE-CACHE LIGHTING (compute pipeline + emissive NEE list)
    // ====================================================================================================
    ID3D12RootSignature lightRootSig;
    ID3D12PipelineState lightPso;
    Dx12FrameCb<LumenLightConstants> lightCb;
    Dx12EmissiveLights lumenEmissive;   // FAZ 3d: world-space emissive-triangle area lights (NEE), built here
    bool lightBuilt;
    bool loggedLight;

    [StructLayout(LayoutKind.Sequential)]
    struct LumenLightConstants {
        public Vector3 SunDir;   public float SunBias;
        public Vector3 SunColor; public float LightCount;
        public uint AtlasSize, PageCount, CardCount, InstanceCount;
        public float EmissiveCount, NeeIntensity, IndirectRays, IndirectIntensity;
        public float FinalValid; public uint FrameIndex; public float SkyIntensity, UseSky;
        public uint AlbedoSrvIdx, NormalSrvIdx, EmissiveSrvIdx, DepthSrvIdx;
        public uint DirectUavIdx, FinalReadSrvIdx, FinalWriteUavIdx, Pad0;
    }

    // ---- per-instance card range root SRV (built alongside the card/page buffers) ----
    ID3D12Resource rangeBuf;   // InstanceCardRange[] (GpuLumenCardScene.InstanceCardRange layout)
    public ulong RangeBufferGpuAddress => rangeBuf?.GPUVirtualAddress ?? 0;

    // ---- dirty tracking (mirrors Dx12LumenScene) ----
    int topologyStamp = -1;
    int transformStamp = -1;
    bool loggedThisStamp;

    // FAZ 3c — CPU-side card + page lists retained from the last (re)build so the capture pass can read each card's
    // page rect + its world ortho frame. (3b discarded them after upload; capture needs them on the CPU.)
    GpuLumenCard[] cardsCpu = Array.Empty<GpuLumenCard>();
    GpuLumenPage[] pagesCpu = Array.Empty<GpuLumenPage>();
    int captureStamp = -1;   // last (rebuild) build stamp the capture ran for; -1 = never captured

    public Dx12LumenCardScene(Dx12Device device) {
        dev = device;
        int sz = (int)EnvF("BALLISTIC_DX12_LUMEN_ATLAS", AtlasSize);
        // Snap to a whole page grid (>= 1 page); clamp to a sane max so a typo can't allocate gigabytes.
        sz = Math.Clamp(sz, PhysicalPageSize, 8192);
        AtlasSize = (sz / PhysicalPageSize) * PhysicalPageSize;
    }

    // Build/refresh the card scene for this frame. Topology-dirty (from Dx12LumenScene's topology stamp, OR our own
    // card-count change) rebuilds the cards + page table; a transform-only change re-derives world cards + re-uploads
    // the card buffer (the page allocation is topology-invariant). The physical atlas is allocated once + persists.
    public void Build(Dx12FrameContext ctx, Dx12SceneAS sceneAS, bool topologyDirty) {
        if (sceneAS is null || !sceneAS.Valid) return;
        EnsureAtlas();

        int s = ComputeTopologyStamp(sceneAS);
        if (topologyDirty || s != topologyStamp || cardBuf == null) {
            topologyStamp = s;
            Rebuild(sceneAS);
            transformStamp = ComputeTransformStamp(sceneAS);
            loggedThisStamp = false;
        } else {
            int ts = ComputeTransformStamp(sceneAS);
            if (ts != transformStamp) {
                transformStamp = ts;
                RefreshTransforms(sceneAS);
            }
        }

        if (!loggedThisStamp) {
            loggedThisStamp = true;
            string line = $"[LumenCards] cards={CardCount} pages={PageCount} dropped={DroppedCards} " +
                          $"atlas={AtlasSize}x{AtlasSize} ({PageGridDim}x{PageGridDim} pages of {PhysicalPageSize}tx) " +
                          $"pageSize={PhysicalPageSize} (FAZ 3b — world cards placed + atlas pages allocated; " +
                          "capture/lighting come in FAZ 3c/3d)";
            Console.WriteLine(line);
            Debugging.Log(line);
        }
    }

    // Full rebuild: gather world cards (per instance), pick a res-level + pack each into the physical atlas, build
    // the page table, set each card's PageId, upload both buffers.
    void Rebuild(Dx12SceneAS sceneAS) {
        var cards = new List<GpuLumenCard>();
        var ranges = new InstanceCardRange[Math.Max(sceneAS.InstanceCount, 1)];
        GatherWorldCards(sceneAS, cards, ranges);
        instanceRanges = ranges;

        var pages = new List<GpuLumenPage>(cards.Count);
        AllocatePages(cards, pages, out int dropped);
        DroppedCards = dropped;

        CardCount = cards.Count;
        PageCount = pages.Count;

        var cardArr = cards.Count > 0 ? cards.ToArray() : new GpuLumenCard[1];
        var pageArr = pages.Count > 0 ? pages.ToArray() : new GpuLumenPage[1];
        cardsCpu = cardArr; pagesCpu = pageArr;   // FAZ 3c: retained for the capture pass (page rects + ortho frames)

        dev.DeferredRelease(cardBuf);
        dev.DeferredRelease(pageBuf);
        cardBuf = dev.CreateUavBuffer<GpuLumenCard>(cardArr, ResourceStates.GenericRead);
        pageBuf = dev.CreateUavBuffer<GpuLumenPage>(pageArr, ResourceStates.GenericRead);
        UploadRanges();
    }

    // FAZ 3d — upload the per-instance card ranges as a GPU StructuredBuffer (root SRV t5) so the lighting compute can
    // map an indirect-ray hit's instance → its card range (for the hit→card→texel radiosity sample).
    void UploadRanges() {
        var arr = instanceRanges.Length > 0 ? instanceRanges : new InstanceCardRange[1];
        dev.DeferredRelease(rangeBuf);
        rangeBuf = dev.CreateUavBuffer<InstanceCardRange>(arr, ResourceStates.GenericRead);
    }

    // Transform-only refresh: re-derive the world cards (origins/axes/extents follow the instance world matrices) and
    // re-run the (deterministic) allocator. Card COUNT/order is topology-invariant, so the page table comes out
    // identical to the last build — but the world card extents changed, so PickResLevel could in principle shift a
    // footprint; replaying the full allocation keeps the PageId↔page table in sync rather than assuming invariance.
    // Only the card buffer is guaranteed stale, but the page table is cheap to re-derive + re-upload for safety.
    void RefreshTransforms(Dx12SceneAS sceneAS) {
        var cards = new List<GpuLumenCard>(CardCount);
        var ranges = new InstanceCardRange[Math.Max(sceneAS.InstanceCount, 1)];
        GatherWorldCards(sceneAS, cards, ranges);
        instanceRanges = ranges;

        var pages = new List<GpuLumenPage>(cards.Count);
        AllocatePages(cards, pages, out int dropped);
        DroppedCards = dropped;
        CardCount = cards.Count;
        PageCount = pages.Count;

        var cardArr = cards.Count > 0 ? cards.ToArray() : new GpuLumenCard[1];
        var pageArr = pages.Count > 0 ? pages.ToArray() : new GpuLumenPage[1];
        cardsCpu = cardArr; pagesCpu = pageArr;   // FAZ 3c
        dev.DeferredRelease(cardBuf);
        dev.DeferredRelease(pageBuf);
        cardBuf = dev.CreateUavBuffer<GpuLumenCard>(cardArr, ResourceStates.GenericRead);
        pageBuf = dev.CreateUavBuffer<GpuLumenPage>(pageArr, ResourceStates.GenericRead);
        UploadRanges();
    }

    // Transform each instance's mesh-local cards into WORLD space (Aurora's exact convention) + record per-instance
    // ranges. Origin = point (w=1); axes = directions (w=0) renormalized; extents scaled by the transformed-axis
    // world length so a scaled instance's card spans the right world size.
    void GatherWorldCards(Dx12SceneAS sceneAS, List<GpuLumenCard> cards, InstanceCardRange[] ranges) {
        int n = sceneAS.InstanceCount;
        for (int i = 0; i < n; i++) {
            int start = cards.Count;
            Mesh mesh = sceneAS.InstanceMesh(i);
            MeshCards mc = mesh?.Cards;
            if (mc is { IsValid: true }) {
                Matrix4x4 w = sceneAS.InstanceWorld(i);
                foreach (MeshCard card in mc.Cards) {
                    Vector3 wo = Vector3.Transform(card.Origin, w);
                    Vector3 wx = Vector3.TransformNormal(card.AxisX, w);
                    Vector3 wy = Vector3.TransformNormal(card.AxisY, w);
                    Vector3 wz = Vector3.TransformNormal(card.AxisZ, w);
                    float lx = wx.Length(), ly = wy.Length(), lz = wz.Length();
                    cards.Add(new GpuLumenCard {
                        Origin = wo, PageId = 0xFFFFFFFFu,   // set by AllocatePages
                        AxisX = lx > 1e-6f ? wx / lx : Vector3.UnitX, ExtentX = card.Extent.X * lx,
                        AxisY = ly > 1e-6f ? wy / ly : Vector3.UnitY, ExtentY = card.Extent.Y * ly,
                        AxisZ = lz > 1e-6f ? wz / lz : Vector3.UnitZ, ExtentZ = card.Extent.Z * lz,
                    });
                }
            }
            ranges[i] = new InstanceCardRange { Offset = (uint)start, Count = (uint)(cards.Count - start) };
        }

        if (!loggedCards && Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CARDS_DUMP") == "1") {
            loggedCards = true;
            for (int k = 0; k < cards.Count; k++) {
                GpuLumenCard cc = cards[k];
                Console.WriteLine($"[LumenCardDump] #{k} O={cc.Origin} Z={cc.AxisZ} ext=({cc.ExtentX:0.##},{cc.ExtentY:0.##},{cc.ExtentZ:0.##})");
            }
        }
    }
    bool loggedCards;

    // Shelf/row allocator: pick a res-level per card from its world size, pack the footprint into the physical atlas,
    // set the card's PageId, and append a GpuLumenPage. Overflow cards are DROPPED (PageId stays 0xFFFFFFFF, counted).
    void AllocatePages(List<GpuLumenCard> cards, List<GpuLumenPage> pages, out int dropped) {
        dropped = 0;
        int cursorX = 0, cursorY = 0, rowHeight = 0;
        for (int c = 0; c < cards.Count; c++) {
            GpuLumenCard card = cards[c];
            int res = PickResLevel(card);
            int size = 1 << res;                 // texels per edge (8..128)

            // Wrap to the next shelf row when this footprint won't fit in the remaining width.
            if (cursorX + size > AtlasSize) {
                cursorX = 0;
                cursorY += rowHeight;
                rowHeight = 0;
            }
            // Atlas full (no vertical room) → drop this card.
            if (cursorY + size > AtlasSize) {
                card.PageId = 0xFFFFFFFFu;
                cards[c] = card;
                dropped++;
                continue;
            }

            uint pageId = (uint)pages.Count;
            pages.Add(new GpuLumenPage {
                AtlasOffsetX = (uint)cursorX, AtlasOffsetY = (uint)cursorY,
                SizeX = (uint)size, SizeY = (uint)size,
                CardId = (uint)c, ResLevel = (uint)res,
            });
            card.PageId = pageId;
            cards[c] = card;

            cursorX += size;
            rowHeight = Math.Max(rowHeight, size);
        }
    }

    // Pick a resolution level from the card's world footprint: 1 texel per CardTileSize world-cm-ish unit, clamped to
    // [MinResLevel..CapResLevel]. v1 keeps every footprint <= one physical page (CapResLevel). The texels-per-edge is
    // a power of two so the page table stays tile-aligned (UE constraint).
    int PickResLevel(in GpuLumenCard card) {
        // Largest in-plane world extent (full size = 2*half-extent) drives the texel budget.
        float maxExtent = MathF.Max(card.ExtentX, card.ExtentY) * 2f;
        // Aim for ~CardTileSize world units per texel → texels = maxExtent / unitPerTexel. unitPerTexel tuned so a
        // ~1 m wall card lands around 64 texels (a reasonable v1 capture density).
        const float unitPerTexel = 0.03125f;             // 1/32 (world unit per texel)
        float texels = MathF.Max(1f, maxExtent / unitPerTexel);
        int res = (int)MathF.Round(MathF.Log2(texels));
        return Math.Clamp(res, MinResLevel, CapResLevel);
    }

    // ====================================================================================================
    // FAZ 3c — CARD CAPTURE
    // ====================================================================================================

    // Capture each card's mesh material attributes into its atlas page. Runs ONCE per (re)build (capturedStamp gate);
    // a static scene captures on the first armed frame and never again. Recorded into the OPEN frame list (ExecuteSync
    // folds onto the frame thread). NO lighting — albedo/card-normal/emissive/card-depth only (3d lights the cache).
    public unsafe void Capture(Dx12FrameContext ctx, Dx12SceneAS sceneAS) {
        if (sceneAS is null || !sceneAS.Valid || CardCount == 0 || PageCount == 0) return;
        Captured = false;
        if (captureStamp == topologyStamp) return;   // already captured this topology — nothing changed
        EnsureCapturePipeline();

        // Map each card id → its page rect (only allocated cards have a page; dropped cards have PageId=0xFFFFFFFF).
        // pagesCpu is indexed by PageId; pagesCpu[p].CardId is the card it belongs to. Build a card→page lookup.
        var cardToPage = new int[CardCount];
        for (int i = 0; i < cardToPage.Length; i++) cardToPage[i] = -1;
        for (int p = 0; p < pagesCpu.Length && p < PageCount; p++) {
            uint cid = pagesCpu[p].CardId;
            if (cid < (uint)CardCount) cardToPage[cid] = p;
        }

        // Resolve, per card, its owning instance (so we know the mesh + renderer). instanceRanges[i] gives the card
        // index range for instance i (in SceneAS instance order).
        int[] cardInstance = new int[CardCount];
        for (int i = 0; i < cardInstance.Length; i++) cardInstance[i] = -1;
        for (int inst = 0; inst < instanceRanges.Length; inst++) {
            var r = instanceRanges[inst];
            for (uint k = 0; k < r.Count; k++) {
                uint ci = r.Offset + k;
                if (ci < (uint)CardCount) cardInstance[ci] = inst;
            }
        }

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;
        int frameSlot = dev.FrameSlot;
        long cbFrameBase = (long)frameSlot * captureCbSlotCount * captureCbSlotSize;
        int cbSlot = 0;
        int capturedCards = 0, capturedDraws = 0, skippedNoMesh = 0;

        captureSrv.Reset();

        dev.ExecuteSync(cl => {
            // All atlases UAV → RenderTarget (the persistent atlas state is UnorderedAccess between passes).
            cl.ResourceBarrierTransition(albedo.Tex,   ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
            cl.ResourceBarrierTransition(normal.Tex,   ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
            cl.ResourceBarrierTransition(emissive.Tex, ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
            cl.ResourceBarrierTransition(depthA.Tex,   ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);

            Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[4];
            rtvs[0] = CaptureRtv(0); rtvs[1] = CaptureRtv(1); rtvs[2] = CaptureRtv(2); rtvs[3] = CaptureRtv(3);
            cl.OMSetRenderTargets(rtvs, captureDsvHandle);

            cl.SetGraphicsRootSignature(captureRootSig);
            cl.SetPipelineState(capturePso);
            cl.SetDescriptorHeaps(captureSrv.Heap);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            for (int c = 0; c < CardCount; c++) {
                int page = cardToPage[c];
                if (page < 0) continue;                       // dropped/unallocated card
                int inst = cardInstance[c];
                if (inst < 0) continue;
                Mesh mesh = sceneAS.InstanceMesh(inst);
                IStaticMeshRenderer renderer = sceneAS.InstanceRenderer(inst);
                if (mesh is null) { skippedNoMesh++; continue; }

                var vb = mesh.VertexBuffer as Dx12Buffer<Vector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<Vector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) { skippedNoMesh++; continue; }

                GpuLumenCard card = cardsCpu[c];
                GpuLumenPage pg = pagesCpu[page];

                // Card ortho view-proj (world → card clip). Eye just outside the front face, looking inward (-AxisZ),
                // up = AxisY. RH lookAt + standard ortho (0..1 depth, near<far) — matches the engine camera convention.
                Vector3 eye = card.Origin + card.AxisZ * card.ExtentZ;
                Vector3 target = card.Origin - card.AxisZ * card.ExtentZ;
                Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, card.AxisY);
                Matrix4x4 proj = Matrix4x4.CreateOrthographic(
                    2f * MathF.Max(card.ExtentX, 1e-4f), 2f * MathF.Max(card.ExtentY, 1e-4f),
                    0f, 2f * MathF.Max(card.ExtentZ, 1e-4f));
                Matrix4x4 viewProj = view * proj;

                // Viewport + scissor = this card's page rect in the atlas (raster is clipped to the page).
                var pageRect = new Vortice.RawRect(
                    (int)pg.AtlasOffsetX, (int)pg.AtlasOffsetY,
                    (int)(pg.AtlasOffsetX + pg.SizeX), (int)(pg.AtlasOffsetY + pg.SizeY));
                cl.RSSetViewport(pg.AtlasOffsetX, pg.AtlasOffsetY, pg.SizeX, pg.SizeY);
                cl.RSSetScissorRect(pageRect);

                // Clear only THIS page rect in each atlas RTV (other cards' pages keep their captures). Depth target is
                // page-sized (PhysicalPageSize²), shared by every card — but the viewport sits at the page's ATLAS
                // offset while the DSV is at 0,0; so the depth target must be at least as large as the largest page +
                // its offset. Since pages can sit anywhere up to AtlasSize, the page-sized depth alone can't cover an
                // offset page. We therefore make the DSV the FULL atlas size (see CreateCaptureTargets) and clear its
                // page rect too.
                for (int rt = 0; rt < 4; rt++)
                    cl.ClearRenderTargetView(rtvs[rt], new Vortice.Mathematics.Color4(0, 0, 0, 0), 1, new[] { pageRect });
                cl.ClearDepthStencilView(captureDsvHandle, ClearFlags.Depth, 1.0f, 0, new[] { pageRect });

                Material mat0 = renderer?.MaterialFor(0);
                int only = renderer?.SubMeshIndex ?? -1;
                int first = only >= 0 ? only : 0;
                int last  = only >= 0 ? only : mesh.SubMeshes.Length - 1;

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                Matrix4x4 model = renderer?.Transform.RenderMatrix ?? sceneAS.InstanceWorld(inst);
                bool drewAny = false;

                for (int s = first; s <= last; s++) {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    if (cbSlot >= captureCbSlotCount) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = renderer?.MaterialFor(s) ?? mat0;
                    if (mat is null) continue;

                    bool hasMetal = mat.Metallic is not null;
                    bool hasRough = mat.Roughness is not null;
                    bool emissiveOn = mat.IsEmissive;
                    Vector4 ec = mat.GetVector(MaterialSemantic.EmissiveColor);
                    var cc = new LumenCaptureConstants {
                        Mvp = Matrix4x4.Transpose(model * viewProj),
                        Model = Matrix4x4.Transpose(model),
                        CardAxisX = card.AxisX, CardAxisY = card.AxisY,
                        CardAxisZ = card.AxisZ, CardExtentZ = card.ExtentZ,
                        CardOrigin = card.Origin,
                        BaseColorFactor = mat.GetVector(MaterialSemantic.BaseColorFactor),
                        EmissiveFactor = new Vector3(ec.X, ec.Y, ec.Z) * mat.GetFloat(MaterialSemantic.EmissiveIntensity),
                        HasEmissive = emissiveOn ? 1f : 0f,
                        Metallic = mat.GetFloat(MaterialSemantic.MetallicFactor),
                        Roughness = mat.GetFloat(MaterialSemantic.RoughnessFactor),
                        NormalStrength = mat.GetFloat(MaterialSemantic.NormalStrength),
                        NormalFlipY = mat.GetFloat(MaterialSemantic.NormalFlipY),
                        HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                        PackedOrm = mat.GetFloat(MaterialSemantic.PackedOrm), Cutout = mat.GetFloat(MaterialSemantic.Cutout),
                    };
                    long cbOff = cbFrameBase + (long)cbSlot * captureCbSlotSize;
                    *(LumenCaptureConstants*)(captureCbMapped + cbOff) = cc;
                    cl.SetGraphicsRootConstantBufferView(0, captureCbRing.GPUVirtualAddress + (ulong)cbOff);

                    int tableStart = captureSrv.AllocateRange(CaptureMaterialSrvCount);
                    BindCaptureSrv(tableStart + 0, mat.GetTexture(MaterialSemantic.DiffuseMap),   TextureType.Diffuse, fallbackDiffuse);
                    BindCaptureSrv(tableStart + 1, mat.GetTexture(MaterialSemantic.NormalMap),    TextureType.Normal, null);
                    BindCaptureSrv(tableStart + 2, mat.GetTexture(MaterialSemantic.MetallicMap),  TextureType.Metallic, null);
                    BindCaptureSrv(tableStart + 3, mat.GetTexture(MaterialSemantic.RoughnessMap), TextureType.Roughness, null);
                    BindCaptureSrv(tableStart + 4, mat.GetTexture(MaterialSemantic.AOMap),        TextureType.AO, null);
                    BindCaptureSrv(tableStart + 5, mat.GetTexture(MaterialSemantic.EmissiveMap),  TextureType.Emissive, null);
                    cl.SetGraphicsRootDescriptorTable(1, captureSrv.Gpu(tableStart));

                    cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                    cbSlot++; capturedDraws++; drewAny = true;
                }
                if (drewAny) capturedCards++;
            }

            // Atlases back to UnorderedAccess (the persistent inter-pass state 3d/trace/debug expect).
            cl.ResourceBarrierTransition(albedo.Tex,   ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(normal.Tex,   ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(emissive.Tex, ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(depthA.Tex,   ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
        });

        captureStamp = topologyStamp;
        Captured = true;
        string line = $"[LumenCapture] captured cards={capturedCards}/{CardCount} draws={capturedDraws} " +
                      $"skippedNoMesh={skippedNoMesh} (FAZ 3c — material attributes rasterized into atlas pages)";
        Console.WriteLine(line);
        Debugging.Log(line);

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CAPTURE_READBACK") == "1" && PageCount > 0)
            ReadbackProof();
    }

    // One-shot CPU readback (ExecuteSyncImmediate, NOT ExecuteSync+Flush per the hard rule) of the ALBEDO atlas page
    // centers — proves the capture wrote non-zero material data into the pages. Gated by an env door (debug only).
    unsafe void ReadbackProof() {
        ResourceDescription rd = albedo.Tex.Description;
        var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
        dev.Device.GetCopyableFootprints(rd, 0, 1, 0, fps, rc, rs, out ulong total);
        int rowPitch = (int)fps[0].Footprint.RowPitch;   // R8G8B8A8 → 4 bytes/texel
        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(total), ResourceStates.CopyDest);

        dev.ExecuteSyncImmediate(cl => {
            cl.ResourceBarrierTransition(albedo.Tex, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fps[0]), 0, 0, 0,
                new TextureCopyLocation(albedo.Tex, 0), null);
            cl.ResourceBarrierTransition(albedo.Tex, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });

        byte* m = readback.Map<byte>(0);
        int probed = Math.Min(PageCount, 12);
        for (int p = 0; p < probed; p++) {
            GpuLumenPage pg = pagesCpu[p];
            int cx = (int)(pg.AtlasOffsetX + pg.SizeX / 2);
            int cy = (int)(pg.AtlasOffsetY + pg.SizeY / 2);
            byte* px = m + (long)cy * rowPitch + (long)cx * 4;
            Console.WriteLine($"[LumenCaptureReadback] page#{p} card={pg.CardId} rect=({pg.AtlasOffsetX},{pg.AtlasOffsetY},{pg.SizeX}x{pg.SizeY}) " +
                              $"center=({cx},{cy}) albedoRGBA=({px[0]},{px[1]},{px[2]},{px[3]})");
        }
        readback.Unmap(0);
    }

    // ====================================================================================================
    // FAZ 3d — LIGHT THE SURFACE CACHE
    // ====================================================================================================

    // Light every allocated card page's texels: direct (sun + punctual + emissive NEE, shadow-rayed) + indirect
    // (radiosity gather of LAST frame's FinalLighting) → write DirectLighting + FinalLighting atlases. Runs EVERY
    // armed frame (lighting is dynamic: multi-bounce accumulates over frames + lights can move). Ping-pongs the two
    // FinalLighting atlases so the radiosity reads last frame's lit cache while writing this frame's.
    public unsafe void LightCards(Dx12FrameContext ctx, Dx12SceneAS sceneAS) {
        if (sceneAS is null || !sceneAS.Valid || CardCount == 0 || PageCount == 0) return;
        EnsureLightPipeline();

        // Build/refresh the emissive-triangle area-light list (NEE). Cached by an instance+emissive stamp, so a
        // static scene builds it once. Pass the SAME renderer set Aurora uses.
        bool neeOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NEE") != "0";
        if (neeOn) lumenEmissive.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        float neeCount = (neeOn && lumenEmissive.Valid) ? lumenEmissive.Count : 0f;
        float neeIntensity = EnvF("BALLISTIC_DX12_LUMEN_NEE_INTENSITY", 1f);

        // Sun: ctx.LightDir is TO-sun after normalize (~0 = night). Punctual lights from the clustered light buffer.
        Vector3 sunDir = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);
        Vector3 sunColor = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.Zero : ctx.LightColor;
        var clustered = ctx.ClusteredLights;
        ulong lightAddr = clustered?.LightBufGpuAddress ?? 0;
        float lightCount = clustered?.LightCount ?? 0;

        float indirectRays = EnvF("BALLISTIC_DX12_LUMEN_INDIRECT_RAYS", 4f);
        float indirectIntensity = EnvF("BALLISTIC_DX12_LUMEN_INDIRECT_INTENSITY", 1f);

        lightCb.Write(new LumenLightConstants {
            SunDir = sunDir, SunBias = 0.03f, SunColor = sunColor, LightCount = lightCount,
            AtlasSize = (uint)AtlasSize, PageCount = (uint)PageCount, CardCount = (uint)CardCount,
            InstanceCount = (uint)instanceRanges.Length,
            EmissiveCount = neeCount, NeeIntensity = neeIntensity,
            IndirectRays = indirectRays, IndirectIntensity = indirectIntensity,
            FinalValid = finalValid ? 1f : 0f, FrameIndex = (uint)ctx.FrameCounter,
            SkyIntensity = 0f, UseSky = 0f,
            AlbedoSrvIdx = (uint)albedo.SrvBindless, NormalSrvIdx = (uint)normal.SrvBindless,
            EmissiveSrvIdx = (uint)emissive.SrvBindless, DepthSrvIdx = (uint)depthA.SrvBindless,
            DirectUavIdx = (uint)directLight.UavBindless,
            FinalReadSrvIdx = (uint)finalLightRead.SrvBindless, FinalWriteUavIdx = (uint)finalLightWrite.UavBindless,
        });

        ulong cbAddr = lightCb.Gpu;
        ulong emissiveAddr = neeCount > 0f ? lumenEmissive.GpuAddress : (lightAddr != 0 ? lightAddr : cardBuf.GPUVirtualAddress);
        ulong lightsAddr = lightAddr != 0 ? lightAddr : cardBuf.GPUVirtualAddress;   // valid filler when no punctual lights
        ulong rangeAddr = rangeBuf?.GPUVirtualAddress ?? cardBuf.GPUVirtualAddress;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        int groups = (AtlasSize + 7) / 8;

        dev.ExecuteSync(cl => {
            // READ atlases (Albedo/Normal/Emissive/Depth + last FinalLighting) → NonPixelShaderResource; WRITE atlases
            // (DirectLighting + this frame's FinalLighting) → UnorderedAccess. Persistent resting state is UAV.
            cl.ResourceBarrierTransition(albedo.Tex,    ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(normal.Tex,    ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(emissive.Tex,  ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(depthA.Tex,    ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(finalLightRead.Tex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            // directLight + finalLightWrite stay UnorderedAccess (their resting state).

            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(lightRootSig);
            cl.SetPipelineState(lightPso);
            cl.SetComputeRootConstantBufferView(0, cbAddr);
            cl.SetComputeRootShaderResourceView(1, sceneAS.TlasAddress);          // t0 TLAS
            cl.SetComputeRootShaderResourceView(2, cardBuf.GPUVirtualAddress);    // t1 Cards
            cl.SetComputeRootShaderResourceView(3, pageBuf.GPUVirtualAddress);    // t2 Pages
            cl.SetComputeRootShaderResourceView(4, lightsAddr);                   // t3 Lights
            cl.SetComputeRootShaderResourceView(5, emissiveAddr);                 // t4 EmissiveLights
            cl.SetComputeRootShaderResourceView(6, rangeAddr);                    // t5 InstanceRanges
            cl.Dispatch((uint)groups, (uint)groups, 1);

            // Read atlases back to UnorderedAccess (the persistent inter-pass state the debug blit + next frame expect).
            cl.ResourceBarrierTransition(albedo.Tex,    ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(normal.Tex,    ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(emissive.Tex,  ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(depthA.Tex,    ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(finalLightRead.Tex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        });

        SwapFinalLighting();
        finalValid = true;

        if (!loggedLight) {
            loggedLight = true;
            string line = $"[LumenLight] lit pages={PageCount} cards={CardCount} atlas={AtlasSize} " +
                          $"sun={(sunColor.LengthSquared() > 0 ? "on" : "off")} lights={lightCount} emissive={neeCount} " +
                          $"indirectRays={indirectRays} finalValid(was)={(!finalValid)} (FAZ 3d — surface cache lit)";
            Console.WriteLine(line);
            Debugging.Log(line);
        }

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_LIGHT_READBACK") == "1" && PageCount > 0)
            LightReadbackProof();
    }

    // Ping-pong the two FinalLighting atlases. Called after each lighting pass: this frame's WRITE becomes next
    // frame's READ (the multi-bounce source), and the now-stale READ becomes the next WRITE target.
    void SwapFinalLighting() => (finalLightRead, finalLightWrite) = (finalLightWrite, finalLightRead);

    // One-shot CPU readback (ExecuteSyncImmediate per the hard rule) of the just-written FinalLighting (= finalLightRead
    // after the swap) page centers — PROVES the cache is lit (emissive card bright; walls carry NEE). Debug-gated.
    unsafe void LightReadbackProof() {
        Atlas lit = finalLightRead;   // SwapFinalLighting already ran → this is the atlas we just wrote
        ResourceDescription rd = lit.Tex.Description;
        var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
        dev.Device.GetCopyableFootprints(rd, 0, 1, 0, fps, rc, rs, out ulong total);
        int rowPitch = (int)fps[0].Footprint.RowPitch;   // R11G11B10_Float → 4 bytes/texel
        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(total), ResourceStates.CopyDest);

        dev.ExecuteSyncImmediate(cl => {
            cl.ResourceBarrierTransition(lit.Tex, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fps[0]), 0, 0, 0,
                new TextureCopyLocation(lit.Tex, 0), null);
            cl.ResourceBarrierTransition(lit.Tex, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });

        byte* m = readback.Map<byte>(0);
        int probed = Math.Min(PageCount, 12);
        int nonZero = 0;
        for (int p = 0; p < probed; p++) {
            GpuLumenPage pg = pagesCpu[p];
            int cx = (int)(pg.AtlasOffsetX + pg.SizeX / 2);
            int cy = (int)(pg.AtlasOffsetY + pg.SizeY / 2);
            uint packed = *(uint*)(m + (long)cy * rowPitch + (long)cx * 4);
            (float r, float g, float b) = UnpackR11G11B10(packed);
            if (r + g + b > 1e-4f) nonZero++;
            Console.WriteLine($"[LumenLightReadback] page#{p} card={pg.CardId} center=({cx},{cy}) " +
                              $"finalRGB=({r:0.###},{g:0.###},{b:0.###})");
        }
        Console.WriteLine($"[LumenLightReadback] {nonZero}/{probed} probed page centers are LIT (non-zero)");
        readback.Unmap(0);
    }

    // Unpack an R11G11B10_Float texel (DXGI packed unsigned float: R/G 5e6m, B 5e5m, no sign).
    static (float, float, float) UnpackR11G11B10(uint v) {
        float Unpack(uint bits, int mbits, int ebits) {
            uint mask = (1u << (mbits + ebits)) - 1u;
            uint x = bits & mask;
            uint e = x >> mbits;
            uint mant = x & ((1u << mbits) - 1u);
            if (e == 0u) return mant == 0u ? 0f : (float)(mant / Math.Pow(2, mbits)) * (float)Math.Pow(2, 1 - 15);
            return (float)((1.0 + mant / Math.Pow(2, mbits)) * Math.Pow(2, (int)e - 15));
        }
        float r = Unpack(v & 0x7FFu, 6, 5);
        float g = Unpack((v >> 11) & 0x7FFu, 6, 5);
        float b = Unpack((v >> 22) & 0x3FFu, 5, 5);
        return (r, g, b);
    }

    unsafe void EnsureLightPipeline() {
        if (lightBuilt) return;
        lightBuilt = true;

        // Root sig: CBV b0 + root SRVs t0 TLAS / t1 cards / t2 pages / t3 lights / t4 emissive / t5 ranges + s0 clamp.
        // HeapDirectlyIndexed so the shader reads the atlas SRV/UAV bindlessly via ResourceDescriptorHeap[] (mirrors
        // GlobalSdfComposite — the SAME bound bindless heap serves the ResourceDescriptorHeap[] reads).
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlas = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cards = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var pages = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var lights = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);
        var emiss = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All);
        var ranges = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All);
        var clamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        lightRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv, tlas, cards, pages, lights, emiss, ranges }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/LumenCardLight.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "LumenCardLight.hlsl");
        lightPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = lightRootSig, ComputeShader = cs });

        lightCb = new Dx12FrameCb<LumenLightConstants>(dev);
        lumenEmissive = new Dx12EmissiveLights(dev);
    }

    void BindCaptureSrv(int slot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback) {
        var dx = (tex as Dx12Texture2D) ?? explicitFallback ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, captureSrv.Cpu(slot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    unsafe void EnsureCapturePipeline() {
        if (captureBuilt) return;
        captureBuilt = true;

        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, CaptureMaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        captureRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable }, new[] { wrap })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/LumenCardCapture.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "LumenCardCapture.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "LumenCardCapture.hlsl");
        captureLayout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        capturePso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = captureRootSig, VertexShader = vs, PixelShader = ps, InputLayout = captureLayout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            // CullNone: cards are one-sided interior surfaces and we want whichever mesh face the ortho view sees.
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] {
                Format.R8G8B8A8_UNorm, Format.R8G8_UNorm, Format.R11G11B10_Float, Format.R16_Float,
            },
            DepthStencilFormat = Format.D32_Float, SampleDescription = new SampleDescription(1, 0),
        });

        // Per-draw CB ring (FramesInFlight copies, indexed by FrameSlot like the main cbRing).
        captureCbSlotSize = (Marshal.SizeOf<LumenCaptureConstants>() + 255) & ~255;
        captureCbSlotCount = 4096;
        long stride = (long)captureCbSlotSize * captureCbSlotCount;
        captureCbRing = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(stride * dev.FramesInFlight)), ResourceStates.GenericRead);
        captureCbMapped = captureCbRing.Map<byte>(0);

        captureSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            captureCbSlotCount * CaptureMaterialSrvCount, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // Allocate the physical atlas textures + their persistent SRV/UAV in the reserved Lumen surface-cache tail. Built
    // once (cross-frame cache, NOT pooled). Cleared to 0 so the debug/capture passes never read uninitialized texels.
    void EnsureAtlas() {
        if (atlasBuilt) return;
        atlasBuilt = true;

        // Slot layout in the reserved tail: each atlas = SRV then UAV, in this fixed order.
        int slot = Dx12BindlessTail.LumenSurfaceCacheTableBase;
        albedo      = CreateAtlas("LumenCardAlbedo",   Format.R8G8B8A8_UNorm, ref slot);
        normal      = CreateAtlas("LumenCardNormal",   Format.R8G8_UNorm,     ref slot);
        emissive    = CreateAtlas("LumenCardEmissive", Format.R11G11B10_Float, ref slot);
        depthA      = CreateAtlas("LumenCardDepth",    Format.R16_Float,      ref slot);
        directLight = CreateAtlas("LumenCardDirect",   Format.R11G11B10_Float, ref slot);
        finalLight  = CreateAtlas("LumenCardFinal",    Format.R11G11B10_Float, ref slot);
        finalLightB = CreateAtlas("LumenCardFinalB",   Format.R11G11B10_Float, ref slot);   // FAZ 3d multi-bounce ping-pong

        // FAZ 3d — initial ping-pong assignment: write finalLight, read finalLightB (cleared → black) this frame.
        finalLightWrite = finalLight;
        finalLightRead  = finalLightB;

        ClearAtlases();
        CreateCaptureTargets();
    }

    // FAZ 3c — build the capture RTVs (Albedo/Normal/Emissive/Depth, fixed order) + the transient page-sized depth.
    // RTV/DSV CPU descriptors live in their OWN non-shader-visible heaps (NOT the bindless heap) — a render target /
    // depth view can't be created in a CBV/SRV/UAV heap. Built once with the atlas (persistent).
    void CreateCaptureTargets() {
        Atlas[] captured = { albedo, normal, emissive, depthA };
        captureRtvHeap = dev.Device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, (uint)captured.Length));
        captureRtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        CpuDescriptorHandle rtvStart = captureRtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int k = 0; k < captured.Length; k++) {
            var h = rtvStart; h.Ptr += (nuint)(k * (int)captureRtvInc);
            dev.Device.CreateRenderTargetView(captured[k].Tex, new RenderTargetViewDescription {
                Format = captured[k].Fmt, ViewDimension = RenderTargetViewDimension.Texture2D,
            }, h);
        }

        // FULL-atlas-size depth: a page's viewport sits at its ATLAS offset, and the DSV shares that coordinate space
        // with the RTVs, so the depth target must span the whole atlas (a page at offset (256,384) writes depth there).
        // Cleared per-page (a RawRect clear) so the cost is the page area, not the full atlas.
        var dDesc = new ResourceDescription {
            Dimension = ResourceDimension.Texture2D,
            Width = (ulong)AtlasSize, Height = (uint)AtlasSize, DepthOrArraySize = 1, MipLevels = 1,
            Format = Format.D32_Float, SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.Unknown, Flags = ResourceFlags.AllowDepthStencil,
        };
        captureDepth = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            dDesc, ResourceStates.DepthWrite, new ClearValue(Format.D32_Float, 1.0f, 0));
        captureDepth.Name = "LumenCardCaptureDepth";
        captureDsvHeap = dev.Device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
        captureDsvHandle = captureDsvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateDepthStencilView(captureDepth, new DepthStencilViewDescription {
            Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D,
        }, captureDsvHandle);
    }

    CpuDescriptorHandle CaptureRtv(int i) {
        var h = captureRtvHeap.GetCPUDescriptorHandleForHeapStart();
        h.Ptr += (nuint)(i * (int)captureRtvInc);
        return h;
    }

    Atlas CreateAtlas(string name, Format fmt, ref int slot) {
        var desc = new ResourceDescription {
            Dimension = ResourceDimension.Texture2D,
            Width = (ulong)AtlasSize, Height = (uint)AtlasSize, DepthOrArraySize = 1, MipLevels = 1,
            Format = fmt, SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.Unknown,
            // 3c rasterizes into these (RTV/UAV) and 3d/trace read them (SRV) → both render-target and UAV.
            Flags = ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess,
        };
        ID3D12Resource tex = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties,
            HeapFlags.None, desc, ResourceStates.UnorderedAccess);
        tex.Name = name;

        // Persistent SRV + UAV in the RESERVED tail (NOT the dynamic Allocate() cursor — the GPU-driven material table
        // Reset() would clobber a cursor slot → typed-mismatch descriptor → page fault → device removed). Written once.
        int srvSlot = slot++;
        int uavSlot = slot++;
        CpuDescriptorHandle srvCpu = Dx12Backend.BindlessHeap.Cpu(srvSlot);
        CpuDescriptorHandle uavCpu = Dx12Backend.BindlessHeap.Cpu(uavSlot);
        dev.Device.CreateShaderResourceView(tex, new ShaderResourceViewDescription {
            Format = fmt, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, srvCpu);
        dev.Device.CreateUnorderedAccessView(tex, null, new UnorderedAccessViewDescription {
            Format = fmt, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0, PlaneSlice = 0 },
        }, uavCpu);

        return new Atlas {
            Tex = tex, Fmt = fmt,
            SrvBindless = srvSlot, UavBindless = uavSlot, SrvCpu = srvCpu, UavCpu = uavCpu,
        };
    }

    // Clear all atlases to 0 once on creation. Done via a transient RTV clear (NOT ClearUnorderedAccessViewFloat,
    // which requires the CPU handle to live in a NON-shader-visible heap while the bindless UAV CPU handle is in the
    // shader-visible heap — a D3D12 rule violation). Every atlas has AllowRenderTarget, so an RTV clear is valid +
    // simple. The atlases are left in UnorderedAccess afterwards (3c's capture rasterize/UAV path transitions them).
    void ClearAtlases() {
        Atlas[] all = { albedo, normal, emissive, depthA, directLight, finalLight, finalLightB };
        using ID3D12DescriptorHeap rtvHeap = dev.Device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, (uint)all.Length));
        CpuDescriptorHandle rtvStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        uint rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        for (int k = 0; k < all.Length; k++) {
            var h = rtvStart; h.Ptr += (nuint)(k * (int)rtvInc);
            dev.Device.CreateRenderTargetView(all[k].Tex, null, h);
        }
        // ExecuteSyncImmediate so the clear completes before the transient RTV heap is disposed at method exit
        // (one-time, creation-path — not a per-frame cost). The RTV CPU descriptors are resolved at record time, but
        // immediate submit removes any doubt about the heap's lifetime vs the open frame list.
        dev.ExecuteSyncImmediate(cl => {
            for (int k = 0; k < all.Length; k++) {
                cl.ResourceBarrierTransition(all[k].Tex, ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
                var h = rtvStart; h.Ptr += (nuint)(k * (int)rtvInc);
                cl.ClearRenderTargetView(h, new Vortice.Mathematics.Color4(0, 0, 0, 0));
                cl.ResourceBarrierTransition(all[k].Tex, ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
            }
        });
    }

    int ComputeTopologyStamp(Dx12SceneAS sceneAS) {
        var h = new HashCode();
        h.Add(sceneAS.InstanceCount);
        for (int i = 0; i < sceneAS.InstanceCount; i++) {
            h.Add(sceneAS.InstanceMesh(i)?.GetHashCode() ?? 0);
            h.Add(sceneAS.InstanceMesh(i)?.Cards?.Count ?? 0);
        }
        return h.ToHashCode();
    }

    int ComputeTransformStamp(Dx12SceneAS sceneAS) {
        var h = new HashCode();
        for (int i = 0; i < sceneAS.InstanceCount; i++) {
            Matrix4x4 w = sceneAS.InstanceWorld(i);
            h.Add(w.M11); h.Add(w.M12); h.Add(w.M13);
            h.Add(w.M21); h.Add(w.M22); h.Add(w.M23);
            h.Add(w.M31); h.Add(w.M32); h.Add(w.M33);
            h.Add(w.M41); h.Add(w.M42); h.Add(w.M43);
        }
        return h.ToHashCode();
    }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    public void Dispose() {
        cardBuf?.Dispose(); cardBuf = null;
        pageBuf?.Dispose(); pageBuf = null;
        rangeBuf?.Dispose(); rangeBuf = null;
        albedo?.Tex?.Dispose(); normal?.Tex?.Dispose(); emissive?.Tex?.Dispose();
        depthA?.Tex?.Dispose(); directLight?.Tex?.Dispose(); finalLight?.Tex?.Dispose();
        finalLightB?.Tex?.Dispose();
        albedo = normal = emissive = depthA = directLight = finalLight = finalLightB = null;
        finalLightRead = finalLightWrite = null;
        lightPso?.Dispose(); lightPso = null;
        lightRootSig?.Dispose(); lightRootSig = null;
        lightCb?.Dispose(); lightCb = null;
        lumenEmissive?.Dispose(); lumenEmissive = null;
        captureRtvHeap?.Dispose(); captureRtvHeap = null;
        captureDsvHeap?.Dispose(); captureDsvHeap = null;
        captureDepth?.Dispose(); captureDepth = null;
        capturePso?.Dispose(); capturePso = null;
        captureRootSig?.Dispose(); captureRootSig = null;
        captureCbRing?.Dispose(); captureCbRing = null;
        captureSrv?.Dispose(); captureSrv = null;
    }
}
