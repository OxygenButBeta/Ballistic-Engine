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
    readonly ID3D12CommandAllocator uiAllocator;
    readonly ID3D12GraphicsCommandList4 uiList;

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

        uiAllocator = dev.Device.CreateCommandAllocator(CommandListType.Direct);
        uiList = dev.Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, uiAllocator, null);
        uiList.Close();   // created open; close so the first Reset is uniform (matches Dx12Device).
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
        swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);
    }

    // Flush the GPU, release backbuffer references (required by ResizeBuffers), resize, recreate RTVs.
    public void Resize(int width, int height) {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        dev.Flush();
        for (int i = 0; i < bufferCount; i++) { backBuffers[i]?.Dispose(); backBuffers[i] = null; }
        swapChain.ResizeBuffers((uint)bufferCount, (uint)width, (uint)height, BackbufferFormat, SwapChainFlags.None);
        Width = width; Height = height;
        CreateBackBufferRtvs();
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
        uiList.Dispose();
        uiAllocator.Dispose();
        if (backBuffers != null)
            foreach (ID3D12Resource b in backBuffers) b?.Dispose();
        rtvHeap?.Dispose();
        swapChain.Dispose();
        factory.Dispose();
    }
}
