using System;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace BallisticEngine.DX12;

// A windowed DX12 present surface: an HWND swapchain (flip-model) the EDITOR draws its ImGui UI into.
// The runtime was offscreen-only (Dx12HeadlessRuntime renders to an RTV and reads it back); this is the
// net-new piece the windowed editor host needs. It REUSES the existing Dx12Device + its single command
// queue (DX12 swapchains are created on the QUEUE, not the device) and slots into the device's fully
// synchronous model: BeginFrame opens a UI command list (the ImGui backend records into it), Present
// closes/executes/flips and blocks on the GPU — exactly like every per-pass ExecuteSync already does, so
// no new frame-in-flight / fence plumbing is introduced (a pipelined ring is a later perf follow-up).
//
// Owns its OWN command allocator + list (separate from the device's render + upload lists) because the UI
// frame spans clear -> ImGui draws -> present, which the device's record-a-lambda ExecuteSync can't model.
public sealed class Dx12SwapChain : IDisposable {
    // Flip-model swapchains require R8G8B8A8_UNORM (NOT _SRGB) and FlipDiscard. The composite already
    // outputs encoded sRGB into a _UNorm ldr, so a straight blit/sample is correct. Matches the ldr format.
    public const Format BackbufferFormat = Format.R8G8B8A8_UNorm;

    readonly Dx12Device dev;
    readonly IDXGIFactory4 factory;
    readonly IDXGISwapChain3 swapChain;
    readonly int bufferCount;

    ID3D12DescriptorHeap rtvHeap;
    ID3D12Resource[] backBuffers;
    CpuDescriptorHandle[] rtvHandles;
    readonly uint rtvIncrement;

    // The UI present command list (clear -> ImGui draws -> backbuffer transitions). Open between
    // BeginFrame and Present; the ImGui DX12 backend records its draws into it.
    // P0c: N-BUFFERED so the PLAYER present (PresentTexture) no longer blocks on dev.Flush() every frame —
    // each present uses a distinct allocator+list, and the recycle gate waits only for the present that used
    // THIS slot last (presentFenceTargets), letting the CPU run ahead of the GPU through the present (the
    // same ring the device's frameFence uses for the render frame). The EDITOR path (BeginFrame/EndFrame)
    // keeps using slot 0 synchronously — it still flushes (ImGui's per-frame upload isn't N-buffered here),
    // so the ring only accelerates the player. The fence/ring is sized to the swapchain buffer count.
    readonly ID3D12CommandAllocator[] uiAllocators;
    readonly ID3D12GraphicsCommandList4[] uiLists;
    readonly ID3D12Fence presentFence;
    readonly System.Threading.AutoResetEvent presentFenceEvent = new(false);
    ulong presentFenceValue;
    readonly ulong[] presentFenceTargets;   // [bufferCount] the presentFence value the GPU reaches when that slot's present is done
    int uiSlot;                              // round-robin present slot (0..bufferCount-1)
    // Slot 0's allocator+list is the EDITOR's BeginFrame/EndFrame surface (synchronous, unchanged).
    ID3D12CommandAllocator uiAllocator => uiAllocators[0];
    ID3D12GraphicsCommandList4 uiList => uiLists[0];

    public int Width { get; private set; }
    public int Height { get; private set; }
    // The command list the ImGui backend records into (valid only between BeginFrame and Present).
    public ID3D12GraphicsCommandList4 CommandList => uiList;

    int currentIndex;

    public Dx12SwapChain(Dx12Device device, IntPtr hwnd, int width, int height, int bufferCount = 2) {
        dev = device;
        this.bufferCount = bufferCount;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        // A retained factory (the device's was scoped/disposed in its ctor). Debug flag false — the swapchain
        // path doesn't need it and the device gates its own debug layer.
        factory = CreateDXGIFactory2<IDXGIFactory4>(false);

        var desc = new SwapChainDescription1 {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = BackbufferFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),   // no MSAA on a flip-model swapchain (TAA is the AA)
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = (uint)bufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        using IDXGISwapChain1 sc1 = factory.CreateSwapChainForHwnd(dev.Queue, hwnd, desc, null, null);
        // We do our own windowing; disable DXGI's Alt+Enter fullscreen so it never surprise-toggles mode.
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        swapChain = sc1.QueryInterface<IDXGISwapChain3>();

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, (uint)bufferCount));
        rtvIncrement = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        CreateBackBufferRtvs();

        uiAllocators = new ID3D12CommandAllocator[bufferCount];
        uiLists = new ID3D12GraphicsCommandList4[bufferCount];
        for (int i = 0; i < bufferCount; i++) {
            uiAllocators[i] = dev.Device.CreateCommandAllocator(CommandListType.Direct);
            uiLists[i] = dev.Device.CreateCommandList<ID3D12GraphicsCommandList4>(
                CommandListType.Direct, uiAllocators[i], null);
            uiLists[i].Close();   // created open; close so the first Reset is uniform (matches Dx12Device).
        }
        presentFence = dev.Device.CreateFence(0, FenceFlags.None);
        presentFenceTargets = new ulong[bufferCount];   // all 0 → the first N presents never wait (slots fresh)
    }

    void CreateBackBufferRtvs() {
        backBuffers = new ID3D12Resource[bufferCount];
        rtvHandles = new CpuDescriptorHandle[bufferCount];
        CpuDescriptorHandle start = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < bufferCount; i++) {
            backBuffers[i] = swapChain.GetBuffer<ID3D12Resource>((uint)i);
            backBuffers[i].Name = $"Swapchain Backbuffer #{i}";
            rtvHandles[i] = new CpuDescriptorHandle(start, i, rtvIncrement);
            dev.Device.CreateRenderTargetView(backBuffers[i], null, rtvHandles[i]);
        }
    }

    // Open the UI command list, take the current backbuffer to RENDER_TARGET, bind + clear it, and set a
    // full-window viewport/scissor. The ImGui backend then records draws into CommandList; Present finishes.
    public void BeginFrame(float r, float g, float b, float a = 1f) {
        currentIndex = (int)swapChain.CurrentBackBufferIndex;
        uiAllocator.Reset();
        uiList.Reset(uiAllocator, null);
        // Flip-model backbuffers rest in PRESENT (== COMMON) between frames; promote to RENDER_TARGET.
        uiList.ResourceBarrierTransition(backBuffers[currentIndex], ResourceStates.Present, ResourceStates.RenderTarget);
        uiList.OMSetRenderTargets(rtvHandles[currentIndex]);
        uiList.ClearRenderTargetView(rtvHandles[currentIndex], new Vortice.Mathematics.Color4(r, g, b, a));
        uiList.RSSetViewport(0, 0, Width, Height);
        uiList.RSSetScissorRect(Width, Height);
    }

    // Close + execute the UI list (so the backbuffer holds the rendered UI) and BLOCK on the GPU, leaving
    // the backbuffer in PRESENT state — ready for an optional readback, then Present(). Synchronous: the
    // device's model already stalls per submit, so this just mirrors it.
    public void EndFrame() {
        uiList.ResourceBarrierTransition(backBuffers[currentIndex], ResourceStates.RenderTarget, ResourceStates.Present);
        uiList.Close();
        dev.Queue.ExecuteCommandList(uiList);
        dev.Flush();
    }

    // Flip the presented backbuffer to the screen. syncInterval 1 = vsync, 0 = uncapped (the host's idle
    // throttle paces it via the window's UpdateFrequency, like the GL path).
    public void Present(bool vsync) {
        CheckPresent(swapChain.Present(vsync ? 1u : 0u, PresentFlags.None));
    }

    // Present returns an HRESULT (PreserveSig) — DON'T swallow it. On a device-removal/reset (e.g. a TDR
    // after a cross-monitor resize) surface the real reason + DRED page-fault instead of letting the next
    // call cascade into a full desktop lock-up. Throws so Program.cs's handler prints the diagnosis.
    void CheckPresent(SharpGen.Runtime.Result r) {
        if (r.Success) return;
        if (r.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code ||
            r.Code == Vortice.DXGI.ResultCode.DeviceReset.Code) {
            Debugging.LogError($"[DX12] Present device-removed: reason={dev.Device.DeviceRemovedReason} " +
                               $"DRED={dev.DrainDredReport()}");
            r.CheckError();   // throw the device-removed HRESULT
        }
        // Non-fatal (e.g. OCCLUDED when minimised) — ignore; the next frame re-presents.
    }

    // The PLAYER present (no ImGui): blit the renderer's final LDR color straight into the backbuffer, then
    // flip. `source` must be R8G8B8A8_UNORM at the SAME size as the backbuffer (the windowed host resizes the
    // renderer to the window) and IN RENDER_TARGET state (the player path leaves ldr there) — it's restored
    // to RenderTarget after, keeping the Dx12OffscreenTarget's own state tracking consistent. One command
    // list + GPU flush (the synchronous model), then Present.
    public void PresentTexture(ID3D12Resource source, bool vsync) {
        // P0c — PIPELINED present (no per-frame dev.Flush). Round-robin over N present slots; the recycle gate
        // waits ONLY for the present that last used THIS slot (presentFenceTargets[uiSlot]) before reusing its
        // allocator, so the CPU runs ahead of the GPU by up to bufferCount presents. Ordering vs the render
        // frame is implicit: the blit and the renderer's writes share dev.Queue (FIFO), so the next frame's
        // render into `source` cannot start on the GPU until this slot's CopyResource(bb, source) has executed
        // — no flush needed to protect `source`. The flip-model swapchain provides its own backpressure
        // (BufferCount images), so an uncapped Present never outruns the display by more than the buffer count.
        int slot = uiSlot;
        uiSlot = (uiSlot + 1) % bufferCount;
        WaitPresentSlot(slot);   // gate: the GPU finished the present that used this allocator last time
        currentIndex = (int)swapChain.CurrentBackBufferIndex;
        ID3D12CommandAllocator alloc = uiAllocators[slot];
        ID3D12GraphicsCommandList4 list = uiLists[slot];
        alloc.Reset();
        list.Reset(alloc, null);
        ID3D12Resource bb = backBuffers[currentIndex];
        list.ResourceBarrierTransition(bb, ResourceStates.Present, ResourceStates.CopyDest);
        list.ResourceBarrierTransition(source, ResourceStates.RenderTarget, ResourceStates.CopySource);
        list.CopyResource(bb, source);
        list.ResourceBarrierTransition(source, ResourceStates.CopySource, ResourceStates.RenderTarget);
        list.ResourceBarrierTransition(bb, ResourceStates.CopyDest, ResourceStates.Present);
        list.Close();
        dev.Queue.ExecuteCommandList(list);
        ulong target = ++presentFenceValue;
        dev.Queue.Signal(presentFence, target);
        presentFenceTargets[slot] = target;
        CheckPresent(swapChain.Present(vsync ? 1u : 0u, PresentFlags.None));
    }

    // Recycle gate for a present slot: block until the GPU has finished the present that last used this slot's
    // allocator (so Reset() can't recycle commands still in flight). No-op the first bufferCount presents
    // (targets are 0) and whenever the GPU already passed the target.
    void WaitPresentSlot(int slot) {
        ulong target = presentFenceTargets[slot];
        if (target == 0 || presentFence.CompletedValue >= target) return;
        presentFence.SetEventOnCompletion(target, presentFenceEvent.SafeWaitHandle.DangerousGetHandle());
        presentFenceEvent.WaitOne();
    }

    // Resize the swapchain back buffers (EF3). ResizeBuffers requires (a) the GPU fully idle and (b)
    // EVERY back-buffer reference released, or the in-flight frame that still binds a back-buffer RTV gets
    // its resource ripped out from under it → device removal (the historical 4K→1080p hang + the dev-PC TDR).
    // The drained-resize sequence, in order:
    //  1. DRAIN every in-flight frame. dev.Flush() is a HARD barrier across ALL three queue fences — the
    //     legacy render `fence`, the pipelined `frameFence` (a P0b-overlap frame is signalled ONLY there, so
    //     waiting the render fence alone would let an overlapped frame still be reading a back buffer), AND
    //     the worker-upload fence. After it returns no frame is in flight, whatever FramesInFlight is.
    //  2. RELEASE every back-buffer reference so nothing is held when ResizeBuffers recycles the buffers.
    //  3. ResizeBuffers (with the 0×0 clamp + same-size early-out below).
    //  4. POST-RESIZE RESET: recreate the RTVs from the NEW back buffers AND drop the cached back-buffer
    //     index — the next BeginFrame/PresentTexture re-reads swapChain.CurrentBackBufferIndex from scratch,
    //     so a stale index can never index a disposed buffer. (frameSlot/frameFence ring state needs no reset:
    //     step 1 fully drained it, so the next BeginFrame's per-slot WaitFrameFence sees an already-completed
    //     — or zero — target and no-ops; it can never block on a value that will never be signalled.)
    // This swapchain is the ONLY resize site (Dx12BallisticEngineWindow.OnResize routes here); the editor's
    // in-window "fullscreen" is an ImGui maximized panel, NOT a DXGI mode change, so it does not resize here.
    // ⚠ GPU-hang rule: verified by the bal-resize-test harness (in-flight Frame() before each Resize over a
    // 0×0/shrink/grow/4K→1080p/same-size sequence, default AND BALLISTIC_DX12_OVERLAP=1) — no device removal.
    public void Resize(int width, int height) {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        dev.Flush();   // (1) hard barrier — render + pipelined-frame + worker-upload fences; GPU fully idle
        // (1b) P0c — dev.Flush drains the device's queues but NOT this swapchain's present fence: a pipelined
        // PresentTexture (CopyResource into a backbuffer) is signalled only on presentFence. Releasing the
        // backbuffers below while that copy is still reading one → device removal. Drain the latest present too.
        WaitPresentSlot((uiSlot + bufferCount - 1) % bufferCount);   // the most-recently submitted present slot
        for (int i = 0; i < bufferCount; i++) { backBuffers[i]?.Dispose(); backBuffers[i] = null; }  // (2)
        try {
            swapChain.ResizeBuffers((uint)bufferCount, (uint)width, (uint)height, BackbufferFormat, SwapChainFlags.None);  // (3)
        }
        catch (Exception e) {
            Debugging.LogError($"[DX12] ResizeBuffers failed ({width}x{height}): {e.Message} " +
                               $"reason={dev.Device.DeviceRemovedReason} DRED={dev.DrainDredReport()}");
            throw;
        }
        Width = width; Height = height;
        CreateBackBufferRtvs();              // (4) new RTVs
        currentIndex = (int)swapChain.CurrentBackBufferIndex;   // (4) re-seed; never read a stale index post-resize
    }

    // Read the CURRENT backbuffer (must be in PRESENT state — call after EndFrame, before Present) back to
    // CPU and write a 24-bit bottom-up BGR BMP, the SAME format the GL editor's glReadPixels harness emits,
    // so the editor's headless screenshot path works identically on DX12. Restores PRESENT after.
    public unsafe void SaveBackbufferBmp(string path) {
        ID3D12Resource bb = backBuffers[currentIndex];
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1];
        var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(bb.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;

        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);

        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(bb, ResourceStates.Present, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fp), 0, 0, 0,
                new TextureCopyLocation(bb, 0), null);
            cl.ResourceBarrierTransition(bb, ResourceStates.CopySource, ResourceStates.Present);
        });

        byte* mapped = readback.Map<byte>(0);
        try { WriteBmp(path, mapped, rowPitch); }
        finally { readback.Unmap(0); }
    }

    // 24-bit BMP, bottom-up BGR, from a top-down RGBA8 source (row pitch 256-aligned). Same emitter as
    // Dx12OffscreenTarget.WriteBmp so cross-backend `bal imgdiff` / rgbstat.py compare directly.
    unsafe void WriteBmp(string path, byte* src, int rowPitch) {
        int w = Width, h = Height;
        int padded = (w * 3 + 3) & ~3;
        int imageSize = padded * h;
        int fileSize = 54 + imageSize;
        var file = new byte[fileSize];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(file, 2);
        BitConverter.GetBytes(54).CopyTo(file, 10);
        BitConverter.GetBytes(40).CopyTo(file, 14);
        BitConverter.GetBytes(w).CopyTo(file, 18);
        BitConverter.GetBytes(h).CopyTo(file, 22);
        file[26] = 1; file[28] = 24;
        BitConverter.GetBytes(imageSize).CopyTo(file, 34);
        for (int y = 0; y < h; y++) {
            byte* srcRow = src + (long)(h - 1 - y) * rowPitch;
            int dstRow = 54 + y * padded;
            for (int x = 0; x < w; x++) {
                file[dstRow + x * 3 + 0] = srcRow[x * 4 + 2];   // B
                file[dstRow + x * 3 + 1] = srcRow[x * 4 + 1];   // G
                file[dstRow + x * 3 + 2] = srcRow[x * 4 + 0];   // R
            }
        }
        System.IO.File.WriteAllBytes(path, file);
    }

    public void Dispose() {
        dev.Flush();
        WaitPresentSlot((uiSlot + bufferCount - 1) % bufferCount);   // drain any pipelined present before freeing lists
        presentFence?.Dispose();
        presentFenceEvent.Dispose();
        if (uiLists != null) foreach (var l in uiLists) l?.Dispose();
        if (uiAllocators != null) foreach (var a in uiAllocators) a?.Dispose();
        if (backBuffers != null)
            foreach (ID3D12Resource b in backBuffers) b?.Dispose();
        rtvHeap?.Dispose();
        swapChain.Dispose();
        factory.Dispose();
    }
}
