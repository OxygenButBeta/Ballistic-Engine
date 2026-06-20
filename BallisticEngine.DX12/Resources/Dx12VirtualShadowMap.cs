using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// VIRTUAL SHADOW MAPS — pragmatic CLIPMAP-ARRAY form (the UE5-VSM equivalent's "resolution follows the
// camera + caching + many levels = effectively unlimited resolution" behaviour, WITHOUT the full sparse
// page-table indirection — see the follow-up note at the foot of this file).
//
// This sits BESIDE Dx12ShadowMap (the 4-cascade CSM), never replacing it: it is built/rendered/sampled ONLY
// when VSM is active (env door BALLISTIC_DX12_VSM=1 or the Shadows-volume UseVirtualShadowMaps flag). When
// inactive the renderer never touches it and the default path stays BYTE-IDENTICAL.
//
// === The clipmap model ===
// N levels (default 12), each a layer in a D32_Float texture ARRAY of a fixed per-level resolution (default
// 2048). Each level is an orthographic light-space "page" CENTERED ON THE CAMERA, snapped to its own texel
// grid. Level 0 covers the smallest world extent (densest texels near the camera); level i covers 2^i × that
// extent. So a single physical pool (the N×res² array) gives near geometry dense shadow texels and far
// geometry coarse ones — the VSM "unlimited resolution" property, expressed as a camera-anchored log2
// clipmap instead of frustum-split cascades.
//
// === Caching (the UE5 win) ===
// Each level re-renders ONLY when its texel-snapped center moved OR the caster geometry stamp changed. A
// static camera => every level is free after the first frame (exactly like the CSM cache, but per clipmap
// level). Because each level is anchored to the camera and snapped to ITS OWN (coarser) texel grid, the FAR
// levels move/refresh far less often than the near ones — most frames only level 0-1 re-render.
//
// === Page table? ===
// In this clipmap-array form the "page table" is implicit: virtual page -> physical page is the identity map
// within each level's layer (the whole level IS resident). The level-select + texel lookup in the shader
// (Vsm.hlsl VsmSunShadow) is the indirection that the sparse form would do through a real page table. The
// matrices reach the shader through a dedicated VsmConstants CB (NOT FrameConstants — so the default cascade
// path's b1 layout is untouched / byte-identical).
public sealed class Dx12VirtualShadowMap : IDisposable {
    public const Format DepthFormat = Format.D32_Float;   // typeless R32: DSV D32 + SRV R32_Float
    public const int MaxLevels = 16;                       // matches the VsmConstants array size + shader cap

    public int Resolution { get; }    // per-level texel resolution (square)
    public int Levels { get; }        // active clipmap levels
    public float Level0Extent { get; }// world half-extent covered by level 0 (level i = 2^i × this)
    public ID3D12Resource Resource { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly uint dsvInc;
    int srvIndex = -1;
    ResourceStates state;

    // Per-level fit state (filled by Fit()). LightMatrices feed BOTH the page render (per level == a cascade)
    // and the shader (uploaded into VsmConstants). The snapped centers drive the cache.
    public readonly Matrix4x4[] LightMatrices = new Matrix4x4[MaxLevels];
    public readonly Vector3[] SnappedCenter = new Vector3[MaxLevels];
    public readonly float[] LevelExtent = new float[MaxLevels];    // world half-extent per level (level-select)

    // Cache bookkeeping (per level): the snapped center + caster stamp at the last render.
    readonly Vector3[] lastSnappedCenter = new Vector3[MaxLevels];
    readonly bool[] levelEverRendered = new bool[MaxLevels];
    int lastCasterStamp = int.MinValue;
    public readonly bool[] LevelDirty = new bool[MaxLevels];   // re-render this level this frame?

    public Dx12VirtualShadowMap(Dx12Device device, int resolution, int levels, float level0Extent) {
        dev = device;
        Resolution = resolution;
        Levels = Math.Clamp(levels, 1, MaxLevels);
        Level0Extent = level0Extent;

        var desc = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)resolution, (uint)resolution,
            arraySize: (ushort)Levels, mipLevels: 1);
        desc.Flags = ResourceFlags.AllowDepthStencil;
        var clear = new ClearValue(DepthFormat, 1.0f, 0);
        Resource = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.DepthWrite, clear);
        Resource.Name = "VirtualShadowClipmap";
        state = ResourceStates.DepthWrite;

        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.DepthStencilView, (uint)Levels));
        dsvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        for (int c = 0; c < Levels; c++) {
            var dsvDesc = new DepthStencilViewDescription {
                Format = DepthFormat,
                ViewDimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayDepthStencilView {
                    MipSlice = 0, FirstArraySlice = (uint)c, ArraySize = 1,
                },
            };
            dev.Device.CreateDepthStencilView(Resource, dsvDesc, DsvHandle(c));
        }

        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = Format.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DArray,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2DArray = new Texture2DArrayShaderResourceView {
                MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = (uint)Levels,
            },
        };
        dev.Device.CreateShaderResourceView(Resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    CpuDescriptorHandle DsvHandle(int level) =>
        new(dsvHeap.GetCPUDescriptorHandleForHeapStart(), level, dsvInc);

    // === Per-frame fit ===
    // Centre every level on the camera, snap to that level's texel grid, build a light-space ortho per level,
    // and mark which levels are DIRTY (snapped centre moved OR caster geometry changed). Mirrors the CSM Fit
    // (texel-snap for stable edges) but anchored to the CAMERA instead of the frustum slab, and log2-extent.
    public void Fit(Vector3 cameraPos, Vector3 lightTravelDir, int casterStamp, bool cacheOn) {
        Vector3 lightDir = Vector3.Normalize(lightTravelDir);
        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        bool casterChanged = casterStamp != lastCasterStamp;

        for (int i = 0; i < Levels; i++) {
            float extent = Level0Extent * MathF.Pow(2f, i);   // world half-extent for this level
            LevelExtent[i] = extent;
            float casterBackup = extent * 2f + 60f;

            // Texel-snap the camera-anchored centre to THIS level's grid (each level snaps coarser).
            Matrix4x4 lightView0 = Matrix4x4.CreateLookAt(cameraPos - lightDir * casterBackup, cameraPos, up);
            float texelSize = extent * 2f / Resolution;
            Vector3 centerLs = Vector3.Transform(cameraPos, lightView0);
            centerLs.X = MathF.Floor(centerLs.X / texelSize) * texelSize;
            centerLs.Y = MathF.Floor(centerLs.Y / texelSize) * texelSize;
            Matrix4x4.Invert(lightView0, out Matrix4x4 invLightView0);
            Vector3 center = Vector3.Transform(centerLs, invLightView0);
            SnappedCenter[i] = center;

            Matrix4x4 lightView = Matrix4x4.CreateLookAt(center - lightDir * casterBackup, center, up);
            // DX ortho (z in [0,1]); System.Numerics CreateOrthographic is the DX convention.
            Matrix4x4 lightProj = Matrix4x4.CreateOrthographic(extent * 2f, extent * 2f, 0.1f,
                casterBackup + extent * 2f);
            LightMatrices[i] = lightView * lightProj;

            // Dirty if: never rendered, cache off, caster geometry changed, OR the snapped centre moved.
            bool moved = !levelEverRendered[i] ||
                         (center - lastSnappedCenter[i]).LengthSquared() > 1e-8f;
            LevelDirty[i] = !cacheOn || casterChanged || moved;
        }
        lastCasterStamp = casterStamp;
    }

    // Mark a level as rendered (call after its depth draws record). Updates the cache key so a static camera
    // skips it next frame.
    public void MarkRendered(int level) {
        levelEverRendered[level] = true;
        lastSnappedCenter[level] = SnappedCenter[level];
    }

    // Bind + (optionally) clear one level's depth layer and record depth-only draws. CLEARS only when the
    // level is dirty (a cached level keeps its prior depth — the VSM caching win). `record` issues the draws.
    public void RenderLevel(ID3D12GraphicsCommandList4 cl, int level, bool clear,
        Action<ID3D12GraphicsCommandList4> record) {
        TransitionTo(cl, ResourceStates.DepthWrite);
        cl.RSSetViewport(0, 0, Resolution, Resolution);
        cl.RSSetScissorRect(Resolution, Resolution);
        CpuDescriptorHandle dsv = DsvHandle(level);
        cl.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty, dsv);
        if (clear) cl.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
        record(cl);
    }

    public void ToShaderResource(ID3D12GraphicsCommandList4 cl) =>
        TransitionTo(cl, ResourceStates.PixelShaderResource);

    public void ToDepthWrite(ID3D12GraphicsCommandList4 cl) =>
        TransitionTo(cl, ResourceStates.DepthWrite);

    void TransitionTo(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (state == target) return;
        cl.ResourceBarrierTransition(Resource, state, target);
        state = target;
    }

    public void Dispose() {
        dsvHeap.Dispose();
        Resource.Dispose();
    }
}

// ============================================================================================================
// FOLLOW-UP TO FULL SPARSE PAGING (what this pragmatic form does NOT do yet)
// ------------------------------------------------------------------------------------------------------------
// This implements the CLIPMAP-ARRAY form: every clipmap level is fully resident in its own array layer. The
// production UE5 VSM instead keeps a small PHYSICAL page pool and only renders the virtual pages a depth-pass
// "page-mark" actually needs (the pixels on screen project into), behind a real page table. To reach that:
//
//   1. VsmMark.hlsl  — a compute pass over the camera G-buffer depth: reconstruct each pixel's world pos,
//      project it into every clipmap level, mark the 128×128 virtual page it lands in as NEEDED (atomic OR
//      into a per-level page-bit buffer).
//   2. VsmAllocate.hlsl — a compute pass that walks the NEEDED bits, KEEPS pages already resident (the cache),
//      frees un-needed ones, and hands fresh physical-pool slots (a free-list / atomic counter) to new pages.
//      Output: a page table (virtual page -> physical page index or NOT_ALLOCATED) per level.
//   3. Page render — instead of clearing+drawing a whole level layer, render ONLY the allocated pages: cull
//      casters per (level,page) light-frustum and scissor the draw to the physical page rect. The existing
//      Dx12GpuDrivenRenderer shadow cull/draw already culls per light frustum; it would gain a per-page
//      scissor + the physical-pool render target.
//   4. Sampling — VsmSunShadow would indirect through the page table (level -> virtual page -> physical page
//      -> texel) instead of the identity layer lookup it does now.
//
// The clipmap fit math, the level-select, the camera-anchored texel snap, the per-level caching, and the
// VsmConstants upload here are all REUSED by the sparse form unchanged — this is a working foundation, not a
// throwaway. The only additions are the two compute passes (mark/allocate), the physical page pool + free
// list, and the page-table indirection in the shader.
// ============================================================================================================
