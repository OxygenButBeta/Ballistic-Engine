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
    public const Format ColorFormat = Format.R8G8B8A8_UNorm;
    public const Format DepthFormat = Format.D32_Float;
    public int Width { get; }
    public int Height { get; }
    public ID3D12Resource RenderTarget { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap rtvHeap;
    readonly CpuDescriptorHandle rtvHandle;
    // Optional depth buffer (created when withDepth) — needed for any 3D pass.
    readonly ID3D12Resource depthTarget;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly CpuDescriptorHandle dsvHandle;
    public bool HasDepth => depthTarget != null;
    // Current resource state of the RT, tracked so transitions are correct.
    ResourceStates state = ResourceStates.RenderTarget;

    public Dx12OffscreenTarget(Dx12Device device, int width, int height, bool withDepth = false) {
        dev = device;
        Width = width;
        Height = height;

        var rtDesc = ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,
            mipLevels: 1, arraySize: 1);
        rtDesc.Flags = ResourceFlags.AllowRenderTarget;
        var clearVal = new ClearValue(ColorFormat, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        RenderTarget = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, rtDesc,
            ResourceStates.RenderTarget, clearVal);

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 1));
        rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateRenderTargetView(RenderTarget, null, rtvHandle);

        if (withDepth) {
            var dDesc = ResourceDescription.Texture2D(DepthFormat, (uint)width, (uint)height,
                mipLevels: 1, arraySize: 1);
            dDesc.Flags = ResourceFlags.AllowDepthStencil;
            var dClear = new ClearValue(DepthFormat, 1.0f, 0);
            depthTarget = dev.Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None, dDesc,
                ResourceStates.DepthWrite, dClear);
            dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.DepthStencilView, 1));
            dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
            dev.Device.CreateDepthStencilView(depthTarget, null, dsvHandle);
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

        dev.ExecuteSync(cl => {
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
        dsvHeap?.Dispose();
        depthTarget?.Dispose();
        rtvHeap.Dispose();
        RenderTarget.Dispose();
    }
}
