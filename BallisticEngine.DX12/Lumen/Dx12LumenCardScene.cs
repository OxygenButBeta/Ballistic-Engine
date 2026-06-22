using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using BallisticEngine;          // Mesh, MeshCard, MeshCards

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
    public ID3D12Resource AlbedoAtlas => albedo?.Tex;
    public ID3D12Resource NormalAtlas => normal?.Tex;
    public ID3D12Resource EmissiveAtlas => emissive?.Tex;
    public ID3D12Resource DepthAtlas => depthA?.Tex;
    public ID3D12Resource DirectLightingAtlas => directLight?.Tex;
    public ID3D12Resource FinalLightingAtlas => finalLight?.Tex;
    public int AlbedoSrvBindless => albedo?.SrvBindless ?? -1;
    public int AlbedoUavBindless => albedo?.UavBindless ?? -1;
    public int FinalLightingSrvBindless => finalLight?.SrvBindless ?? -1;
    bool atlasBuilt;

    public bool Valid => CardCount > 0 && cardBuf != null;

    // ---- dirty tracking (mirrors Dx12LumenScene) ----
    int topologyStamp = -1;
    int transformStamp = -1;
    bool loggedThisStamp;

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

        dev.DeferredRelease(cardBuf);
        dev.DeferredRelease(pageBuf);
        cardBuf = dev.CreateUavBuffer<GpuLumenCard>(cardArr, ResourceStates.GenericRead);
        pageBuf = dev.CreateUavBuffer<GpuLumenPage>(pageArr, ResourceStates.GenericRead);
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
        dev.DeferredRelease(cardBuf);
        dev.DeferredRelease(pageBuf);
        cardBuf = dev.CreateUavBuffer<GpuLumenCard>(cardArr, ResourceStates.GenericRead);
        pageBuf = dev.CreateUavBuffer<GpuLumenPage>(pageArr, ResourceStates.GenericRead);
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

        ClearAtlases();
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
        Atlas[] all = { albedo, normal, emissive, depthA, directLight, finalLight };
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
        albedo?.Tex?.Dispose(); normal?.Tex?.Dispose(); emissive?.Tex?.Dispose();
        depthA?.Tex?.Dispose(); directLight?.Tex?.Dispose(); finalLight?.Tex?.Dispose();
        albedo = normal = emissive = depthA = directLight = finalLight = null;
    }
}
