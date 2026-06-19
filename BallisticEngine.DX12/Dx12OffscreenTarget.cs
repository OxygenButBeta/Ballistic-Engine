using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// An offscreen RGBA8 render target + RTV, with GPU->CPU readback to a BMP. This is the DX12 equivalent
// of the GL screenshot path (glReadPixels) the whole verification harness depends on — DX12 has no
// ReadPixels, so readback is a CopyTextureRegion into a readback heap, Map, memcpy (DX12Migration.md
// Phase 1, the single highest-leverage thing to get right). No window needed.
public sealed class Dx12OffscreenTarget : IDisposable {
    // Default LDR backbuffer format (final composite / readback). The HDR scene target overrides it with
    // R16G16B16A16_Float via the ctor `colorFormat` arg.
    public const Format ColorFormat = Format.R8G8B8A8_UNorm;
    public const Format HdrFormat = Format.R16G16B16A16_Float;
    public const Format DepthFormat = Format.D32_Float;
    public Format Format { get; }          // this target's actual color format
    public int Width { get; }
    public int Height { get; }
    public ID3D12Resource RenderTarget { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap rtvHeap;
    readonly CpuDescriptorHandle rtvHandle;
    // Color as a shader resource — for the composite pass that reads the HDR scene color. -1 until first
    // ColorToShaderResource()/SrvCpu use; lazily created. Lives in Dx12Backend.SrvStore.
    int colorSrvIndex = -1;
    public CpuDescriptorHandle ColorSrvCpu => Dx12Backend.SrvStore.Cpu(colorSrvIndex);
    // Optional depth buffer (created when withDepth) — needed for any 3D pass.
    readonly ID3D12Resource depthTarget;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly CpuDescriptorHandle dsvHandle;
    public bool HasDepth => depthTarget != null;
    // Depth as a shader resource (R32_Float SRV over the typeless depth) — for post passes that read
    // scene depth (volumetric fog). Allocated in Dx12Backend.SrvStore; -1 until withDepth.
    int depthSrvIndex = -1;
    public CpuDescriptorHandle DepthSrvCpu => Dx12Backend.SrvStore.Cpu(depthSrvIndex);
    public ID3D12Resource DepthResource => depthTarget;
    ResourceStates depthState = ResourceStates.DepthWrite;
    // Current resource state of the RT, tracked so transitions are correct.
    ResourceStates state = ResourceStates.RenderTarget;

    // PHASE-2 V2: when this target's color RT is a PLACED resource (aliased onto a pool heap), keep a handle to
    // its heap + byte offset so the pool can identify the previous tenant for an aliasing barrier. Null/0 for the
    // default committed path (no aliasing) — byte-identical to pre-V2. Set only when constructed via the pool.
    public ID3D12Heap PlacedHeap { get; }
    public ulong PlacedOffset { get; }
    public bool IsPlaced => PlacedHeap != null;
    // The CPU descriptor for a UAV view of the color RT, when the pool created one (RT-GI/OIDN write the GI scratch
    // via a UAV). The committed path lazily creates UAVs per-pass; placed targets don't need this today (passes
    // still build their own UAVs), so it stays informational. Reserved for V3/V4.

    public Dx12OffscreenTarget(Dx12Device device, int width, int height, bool withDepth = false,
        Format? colorFormat = null, bool colorReadable = false, bool allowUav = false,
        ID3D12Heap placedHeap = null, ulong placedOffset = 0) {
        dev = device;
        Width = width;
        Height = height;
        Format = colorFormat ?? ColorFormat;

        var rtDesc = ResourceDescription.Texture2D(Format, (uint)width, (uint)height,
            mipLevels: 1, arraySize: 1);
        // AllowUnorderedAccess for targets a compute pass (e.g. the FSR upscaler) writes via UAV.
        rtDesc.Flags = ResourceFlags.AllowRenderTarget | (allowUav ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None);
        var clearVal = new ClearValue(Format, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        // PHASE-2 V2 PLACED PATH: when the render-target pool supplies a heap + offset, the color RT is a PLACED
        // resource on shared (aliasable) memory instead of a committed resource with its own implicit heap. This is
        // the ONLY change for aliasing — every other code path (RTV/SRV/transitions/readback) is byte-identical
        // because they operate on the ID3D12Resource handle, which is the same shape either way. The pool guarantees
        // no two PLACED resources whose lifetimes OVERLAP share an offset (so committed-vs-placed is a memory-
        // location change only). The initial state is RenderTarget exactly as the committed path (placed resources
        // start UNINITIALIZED, but every pooled target is FULLY OVERWRITTEN before it is read — the V2 read-before-
        // write audit, the load-bearing safety net — so the leftover tenant garbage is never observed).
        if (placedHeap != null) {
            PlacedHeap = placedHeap;
            PlacedOffset = placedOffset;
            ClearValue? cv = clearVal;   // bind the Nullable<ClearValue> CreatePlacedResource<T> overload (returns T)
            RenderTarget = dev.Device.CreatePlacedResource<ID3D12Resource>(
                placedHeap, placedOffset, rtDesc, ResourceStates.RenderTarget, cv);
        } else {
            RenderTarget = dev.Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None, rtDesc,
                ResourceStates.RenderTarget, clearVal);
        }

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 1));
        rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateRenderTargetView(RenderTarget, null, rtvHandle);

        // The HDR scene target is sampled by the composite pass — give it a color SRV.
        if (colorReadable) {
            colorSrvIndex = Dx12Backend.SrvStore.Allocate();
            dev.Device.CreateShaderResourceView(RenderTarget, new ShaderResourceViewDescription {
                Format = Format,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            }, Dx12Backend.SrvStore.Cpu(colorSrvIndex));
        }

        if (withDepth) {
            // Typeless so the SAME resource is both a D32 DSV and an R32_Float SRV (post passes read depth).
            var dDesc = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)width, (uint)height,
                mipLevels: 1, arraySize: 1);
            dDesc.Flags = ResourceFlags.AllowDepthStencil;
            var dClear = new ClearValue(DepthFormat, 1.0f, 0);
            depthTarget = dev.Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None, dDesc,
                ResourceStates.DepthWrite, dClear);
            dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.DepthStencilView, 1));
            dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
            dev.Device.CreateDepthStencilView(depthTarget,
                new DepthStencilViewDescription {
                    Format = DepthFormat, ViewDimension = DepthStencilViewDimension.Texture2D,
                }, dsvHandle);

            depthSrvIndex = Dx12Backend.SrvStore.Allocate();
            dev.Device.CreateShaderResourceView(depthTarget, new ShaderResourceViewDescription {
                Format = Format.R32_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            }, Dx12Backend.SrvStore.Cpu(depthSrvIndex));
        }
    }

    // Clear to a color (linear-ish RGBA 0..1), and the depth buffer to far (1.0) when present.
    public void Clear(float r, float g, float b, float a = 1f) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            BindTargets(cl);
            cl.ClearRenderTargetView(rtvHandle, new Vortice.Mathematics.Color4(r, g, b, a));
            if (HasDepth)
                cl.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);
        });
    }

    // Record arbitrary draw commands against this RTV (+DSV) (viewport/scissor set up here). `record`
    // runs with the targets bound and the RT in RenderTarget state.
    public void RenderInto(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            BindTargets(cl);
            record(cl);
        });
    }

    // Post pass: bind ONLY the color RTV (no depth) + the viewport, run `record` (a fullscreen draw
    // that reads depth/shadows as SRVs and blends over color). Depth must already be in
    // PixelShaderResource (call DepthToShaderResource first). Separate ExecuteSync.
    public void RenderColorOnly(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            cl.OMSetRenderTargets(rtvHandle);   // no DSV — post pass doesn't test/write depth
            record(cl);
        });
    }

    // Like RenderColorOnly but clears the color to black first — the deferred lighting pass discards sky
    // pixels (depth==far), so they must be a known black for the subsequent sky pass to overwrite.
    public void RenderColorOnlyCleared(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            cl.OMSetRenderTargets(rtvHandle);
            cl.ClearRenderTargetView(rtvHandle, new Vortice.Mathematics.Color4(0, 0, 0, 1));
            record(cl);
        });
    }

    // Like RenderColorOnly but also binds an EXTERNAL depth-stencil view (the deferred sky pass draws
    // into the HDR color while depth-testing against the G-buffer depth, which this target doesn't own).
    // The external depth must already be in a DSV-bindable state (DepthRead for a no-write LEqual test).
    public void RenderColorWithExternalDepth(CpuDescriptorHandle dsv, Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            cl.OMSetRenderTargets(new[] { rtvHandle }, dsv);
            record(cl);
        });
    }

    // Copy another same-size/format target's color into THIS target's color (e.g. SSR combine wrote to a
    // scratch, copy it back so the rest of the pipeline keeps reading this target). Handles transitions.
    public void CopyColorFrom(Dx12OffscreenTarget src) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.CopyDest);
            src.TransitionTo(cl, ResourceStates.CopySource);
            cl.CopyResource(RenderTarget, src.RenderTarget);
            TransitionTo(cl, ResourceStates.RenderTarget);
            src.TransitionTo(cl, ResourceStates.RenderTarget);
        });
    }

    // Color state transitions: the composite reads the HDR scene color as an SRV.
    public void ColorToShaderResource() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.PixelShaderResource));
    }
    public void ColorToRenderTarget() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.RenderTarget));
    }
    // For a COMPUTE shader to read the color as an SRV (e.g. the OIDN GPU pack reads the GI texture).
    public void ColorToNonPixelShaderResource() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.NonPixelShaderResource));
    }
    // For a UAV-capable target a compute pass writes (FSR output). Created with allowUav.
    public void ColorToUnorderedAccess() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.UnorderedAccess));
    }
    // Transition the color INSIDE a caller-supplied command list (state-tracked, idempotent), so a pass can do a
    // multi-step sequence (e.g. SRV-read → CopyDest → SRV) atomically in ONE list — separate ColorToX ExecuteSync
    // calls split the barriers across submits. Used by the RTAO copy-back (Dx12RtaoPass).
    public void ColorTransitionInList(ID3D12GraphicsCommandList4 cl, ResourceStates target) => TransitionTo(cl, target);

    // Depth state transitions for post passes that read scene depth as an SRV.
    public void DepthToShaderResource() {
        if (!HasDepth || depthState == ResourceStates.PixelShaderResource) return;
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(depthTarget, depthState, ResourceStates.PixelShaderResource);
            depthState = ResourceStates.PixelShaderResource;
        });
    }
    public void DepthToWrite() {
        if (!HasDepth || depthState == ResourceStates.DepthWrite) return;
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(depthTarget, depthState, ResourceStates.DepthWrite);
            depthState = ResourceStates.DepthWrite;
        });
    }

    // Clear color+depth, then record draws — the whole frame in ONE command-list submission (one
    // ExecuteSync), which the per-frame renderer wants (vs Clear() + RenderInto() = two submits).
    public void RenderIntoCleared(float r, float g, float b, Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            BindTargets(cl);
            cl.ClearRenderTargetView(rtvHandle, new Vortice.Mathematics.Color4(r, g, b, 1f));
            if (HasDepth)
                cl.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);
            record(cl);
        });
    }

    // PHASE-2 V2: when this is a PLACED (aliased) target, the pool calls this right after the aliasing barrier
    // (same recorded list, the pool's ExecuteSync) to satisfy the D3D12 placed-RT INITIALIZATION requirement: a
    // freshly-(re)activated placed render target is uninitialized; the debug layer requires a Discard/Clear/Copy
    // before the first draw that uses it (RenderTargetOrDepthStencilResouceNotInitialized otherwise). Discard =
    // "prior contents are undefined" — exactly correct here because the consuming pass FULLY OVERWRITES this RT
    // before reading it (the V2 read-before-write audit). Transitions to RenderTarget first (idempotent — discard
    // requires the RT/DEPTH_WRITE state) so it is valid regardless of the state the previous tenant left it in.
    // No-op for a committed target (only the pool calls this, only on placed targets).
    public void DiscardForAlias(ID3D12GraphicsCommandList4 cl) {
        // DiscardResource requires the resource be in RENDER_TARGET (or DEPTH_WRITE). Transition from the C#-tracked
        // state (which matches the GPU reality — the producing pass left it where it ended last frame, OR
        // MarkAliasedOut reset it to RenderTarget when a later pass decayed it), then discard (the placed-RT
        // initialization hint that satisfies RenderTargetOrDepthStencilResouceNotInitialized).
        TransitionTo(cl, ResourceStates.RenderTarget);
        cl.DiscardResource(RenderTarget);
    }

    void BindTargets(ID3D12GraphicsCommandList4 cl) {
        if (HasDepth)
            cl.OMSetRenderTargets(rtvHandle, dsvHandle);
        else
            cl.OMSetRenderTargets(rtvHandle);
    }

    void TransitionTo(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (state == target) return;
        cl.ResourceBarrierTransition(RenderTarget, state, target);
        state = target;
    }

    // Read the RT back to CPU and write a 24-bit BMP (bottom-up, BGR) — the SAME format the GL harness
    // emits, so bal imgdiff / rgbstat.py compare cross-backend frames directly.
    public unsafe void SaveBmp(string path) {
        // Placed-footprint of subresource 0 (row pitch is 256-byte aligned per the D3D12 copy rule).
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1];
        var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint footprint = footprints[0];
        int rowPitch = (int)footprint.Footprint.RowPitch;

        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);

        dev.ExecuteSyncImmediate(cl => {   // readback: must flush an open pipelined frame so the copy sees it
            TransitionTo(cl, ResourceStates.CopySource);
            var dst = new TextureCopyLocation(readback, footprint);
            var src = new TextureCopyLocation(RenderTarget, 0);
            cl.CopyTextureRegion(dst, 0, 0, 0, src, null);
            TransitionTo(cl, ResourceStates.RenderTarget);
        });

        byte* mapped = readback.Map<byte>(0);
        try {
            WriteBmp(path, mapped, rowPitch);
        } finally {
            readback.Unmap(0);
        }
    }

    // Read the RGB channels of this RGBA16F target back to a CPU float array (length >= Width*Height*3),
    // converting half->float. For the OIDN denoise round-trip (D3D12 texture -> host -> OIDN). Restores the
    // target to RenderTarget after. Slow (blocking readback) — the zero-copy D3D12<->HIP path is the perf
    // follow-up. Assumes the R16G16B16A16_Float format (8 bytes/pixel).
    // Cached readback/upload heaps — CreateCommittedResource on a readback/upload heap every frame was a
    // dominant cost of the OIDN host round-trip; reuse one buffer of each per target (recreated on resize).
    ID3D12Resource cachedReadback, cachedUpload;
    ulong cachedReadbackBytes, cachedUploadBytes;

    public unsafe void ReadColorRgb(float[] dst) {
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;
        if (cachedReadback == null || cachedReadbackBytes != totalBytes) {
            cachedReadback?.Dispose();
            cachedReadback = dev.Device.CreateCommittedResource(
                HeapProperties.ReadbackHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
            cachedReadbackBytes = totalBytes;
        }
        ID3D12Resource readback = cachedReadback;
        dev.ExecuteSyncImmediate(cl => {   // readback: flush an open pipelined frame so the copy sees this frame
            TransitionTo(cl, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fp), 0, 0, 0,
                new TextureCopyLocation(RenderTarget, 0), null);
            TransitionTo(cl, ResourceStates.RenderTarget);
        });
        byte* mapped = readback.Map<byte>(0);
        try {
            int w = Width, h = Height;
            for (int y = 0; y < h; y++) {
                Half* row = (Half*)(mapped + (long)y * rowPitch);
                int o = y * w * 3;
                for (int x = 0; x < w; x++) {
                    dst[o + x * 3 + 0] = (float)row[x * 4 + 0];
                    dst[o + x * 3 + 1] = (float)row[x * 4 + 1];
                    dst[o + x * 3 + 2] = (float)row[x * 4 + 2];
                }
            }
        } finally { readback.Unmap(0); }
    }

    // Upload a CPU float RGB array (length >= Width*Height*3) into this RGBA16F target (alpha = 1),
    // converting float->half. Leaves the target in PixelShaderResource (ready to sample). Pairs with
    // ReadColorRgb for the OIDN round-trip.
    public unsafe void WriteColorRgb(float[] src) {
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;
        if (cachedUpload == null || cachedUploadBytes != totalBytes) {
            cachedUpload?.Dispose();
            cachedUpload = dev.Device.CreateCommittedResource(
                HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);
            cachedUploadBytes = totalBytes;
        }
        ID3D12Resource upload = cachedUpload;
        byte* mapped = upload.Map<byte>(0);
        int w = Width, h = Height;
        Half one = (Half)1f;
        for (int y = 0; y < h; y++) {
            Half* row = (Half*)(mapped + (long)y * rowPitch);
            int o = y * w * 3;
            for (int x = 0; x < w; x++) {
                row[x * 4 + 0] = (Half)src[o + x * 3 + 0];
                row[x * 4 + 1] = (Half)src[o + x * 3 + 1];
                row[x * 4 + 2] = (Half)src[o + x * 3 + 2];
                row[x * 4 + 3] = one;
            }
        }
        upload.Unmap(0);
        dev.ExecuteSyncImmediate(cl => {   // CPU→GPU upload mid-frame (OIDN CPU path): flush so ordering holds
            TransitionTo(cl, ResourceStates.CopyDest);
            cl.CopyTextureRegion(new TextureCopyLocation(RenderTarget, 0), 0, 0, 0,
                new TextureCopyLocation(upload, fp), null);
            TransitionTo(cl, ResourceStates.PixelShaderResource);
        });
    }

    // Read an RGBA8 (R8G8B8A8_UNorm) color target back to a tightly-packed CPU byte[] (w*h*4), TOP-DOWN
    // (row 0 = top). For the editor's mesh/material thumbnail previews (render to this target, read back).
    // Restores RenderTarget after. Assumes the default ColorFormat (4 bytes/pixel).
    public unsafe byte[] ReadColorRgba8() {
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;

        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);

        dev.ExecuteSyncImmediate(cl => {   // readback (editor thumbnail): flush any open frame before the copy
            TransitionTo(cl, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fp), 0, 0, 0,
                new TextureCopyLocation(RenderTarget, 0), null);
            TransitionTo(cl, ResourceStates.RenderTarget);
        });

        var dst = new byte[Width * Height * 4];
        byte* mapped = readback.Map<byte>(0);
        try {
            for (int y = 0; y < Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    (IntPtr)(mapped + (long)y * rowPitch), dst, y * Width * 4, Width * 4);
        } finally { readback.Unmap(0); }
        return dst;
    }

    unsafe void WriteBmp(string path, byte* src, int rowPitch) {
        int w = Width, h = Height;
        int rowBytes = w * 3;
        int padded = (rowBytes + 3) & ~3;          // BMP rows are 4-byte aligned
        int imageSize = padded * h;
        int fileSize = 54 + imageSize;
        var file = new byte[fileSize];

        // BITMAPFILEHEADER + BITMAPINFOHEADER (24-bit, bottom-up).
        file[0] = (byte)'B'; file[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(file, 2);
        BitConverter.GetBytes(54).CopyTo(file, 10);   // pixel data offset
        BitConverter.GetBytes(40).CopyTo(file, 14);   // info header size
        BitConverter.GetBytes(w).CopyTo(file, 18);
        BitConverter.GetBytes(h).CopyTo(file, 22);    // positive => bottom-up
        file[26] = 1;                                  // planes
        file[28] = 24;                                 // bpp
        BitConverter.GetBytes(imageSize).CopyTo(file, 34);

        // Source is RGBA8 row-major top-down (row pitch aligned); BMP is bottom-up BGR.
        for (int y = 0; y < h; y++) {
            byte* srcRow = src + (long)(h - 1 - y) * rowPitch;
            int dstRow = 54 + y * padded;
            for (int x = 0; x < w; x++) {
                byte r = srcRow[x * 4 + 0];
                byte g = srcRow[x * 4 + 1];
                byte b = srcRow[x * 4 + 2];
                file[dstRow + x * 3 + 0] = b;
                file[dstRow + x * 3 + 1] = g;
                file[dstRow + x * 3 + 2] = r;
            }
        }
        System.IO.File.WriteAllBytes(path, file);
    }

    public void Dispose() {
        cachedReadback?.Dispose();
        cachedUpload?.Dispose();
        dsvHeap?.Dispose();
        depthTarget?.Dispose();
        rtvHeap.Dispose();
        RenderTarget.Dispose();
    }
}
