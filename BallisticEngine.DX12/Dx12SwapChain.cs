using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12SwapChain : IDisposable {
    public const Format BackbufferFormat = Format.R8G8B8A8_UNorm;

    readonly Dx12Device dev;
    readonly IDXGIFactory4 factory;
    readonly IDXGISwapChain3 swapChain;
    readonly int bufferCount;

    ID3D12DescriptorHeap rtvHeap;
    ID3D12Resource[] backBuffers;
    CpuDescriptorHandle[] rtvHandles;
    readonly uint rtvIncrement;

    readonly ID3D12CommandAllocator[] uiAllocators;
    readonly ID3D12GraphicsCommandList4[] uiLists;
    readonly ID3D12Fence presentFence;
    readonly System.Threading.AutoResetEvent presentFenceEvent = new(false);
    ulong presentFenceValue;
    readonly ulong[] presentFenceTargets;

    int uiSlot;

    ID3D12CommandAllocator uiAllocator => uiAllocators[0];
    ID3D12GraphicsCommandList4 uiList => uiLists[0];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D12GraphicsCommandList4 CommandList => uiList;

    int currentIndex;

    public Dx12SwapChain(Dx12Device device, IntPtr hwnd, int width, int height, int bufferCount = 2) {
        dev = device;
        this.bufferCount = bufferCount;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        factory = CreateDXGIFactory2<IDXGIFactory4>(false);

        var desc = new SwapChainDescription1 {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = BackbufferFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = (uint)bufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        using IDXGISwapChain1 sc1 = factory.CreateSwapChainForHwnd(dev.Queue, hwnd, desc, null, null);
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
            uiLists[i].Close();
        }
        presentFence = dev.Device.CreateFence(0, FenceFlags.None);
        presentFenceTargets = new ulong[bufferCount];
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

    public void BeginFrame(float r, float g, float b, float a = 1f) {
        currentIndex = (int)swapChain.CurrentBackBufferIndex;
        uiAllocator.Reset();
        uiList.Reset(uiAllocator, null);
        uiList.ResourceBarrierTransition(backBuffers[currentIndex], ResourceStates.Present, ResourceStates.RenderTarget);
        uiList.OMSetRenderTargets(rtvHandles[currentIndex]);
        uiList.ClearRenderTargetView(rtvHandles[currentIndex], new Vortice.Mathematics.Color4(r, g, b, a));
        uiList.RSSetViewport(0, 0, Width, Height);
        uiList.RSSetScissorRect(Width, Height);
    }

    public void EndFrame() {
        uiList.ResourceBarrierTransition(backBuffers[currentIndex], ResourceStates.RenderTarget, ResourceStates.Present);
        uiList.Close();
        dev.Queue.ExecuteCommandList(uiList);
        dev.Flush();
    }

    public void Present(bool vsync) {
        CheckPresent(swapChain.Present(vsync ? 1u : 0u, PresentFlags.None));
    }

    void CheckPresent(SharpGen.Runtime.Result r) {
        if (r.Success) return;
        if (r.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code ||
            r.Code == Vortice.DXGI.ResultCode.DeviceReset.Code) {
            Debugging.LogError($"[DX12] Present device-removed: reason={dev.Device.DeviceRemovedReason} " +
                               $"DRED={dev.DrainDredReport()}");
            r.CheckError();
        }
    }

    public void PresentTexture(ID3D12Resource source, bool vsync) {
        int slot = uiSlot;
        uiSlot = (uiSlot + 1) % bufferCount;
        WaitPresentSlot(slot);
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

    void WaitPresentSlot(int slot) {
        ulong target = presentFenceTargets[slot];
        if (target == 0 || presentFence.CompletedValue >= target) return;
        presentFence.SetEventOnCompletion(target, presentFenceEvent.SafeWaitHandle.DangerousGetHandle());
        presentFenceEvent.WaitOne();
    }

    public void Resize(int width, int height) {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        dev.Flush();
        WaitPresentSlot((uiSlot + bufferCount - 1) % bufferCount);
        for (int i = 0; i < bufferCount; i++) { backBuffers[i]?.Dispose(); backBuffers[i] = null; }

        try {
            swapChain.ResizeBuffers((uint)bufferCount, (uint)width, (uint)height, BackbufferFormat, SwapChainFlags.None);
        }
        catch (Exception e) {
            Debugging.LogError($"[DX12] ResizeBuffers failed ({width}x{height}): {e.Message} " +
                               $"reason={dev.Device.DeviceRemovedReason} DRED={dev.DrainDredReport()}");
            throw;
        }
        Width = width; Height = height;
        CreateBackBufferRtvs();
        currentIndex = (int)swapChain.CurrentBackBufferIndex;
    }

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
                file[dstRow + x * 3 + 0] = srcRow[x * 4 + 2];
                file[dstRow + x * 3 + 1] = srcRow[x * 4 + 1];
                file[dstRow + x * 3 + 2] = srcRow[x * 4 + 0];
            }
        }
        System.IO.File.WriteAllBytes(path, file);
    }

    public void Dispose() {
        dev.Flush();
        WaitPresentSlot((uiSlot + bufferCount - 1) % bufferCount);
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
