using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12OffscreenTarget : IDisposable {
    public const Format ColorFormat = Format.R8G8B8A8_UNorm;
    public const Format HdrFormat = Format.R16G16B16A16_Float;
    public const Format DepthFormat = Format.D32_Float;
    public Format Format { get; }
    public int Width { get; }
    public int Height { get; }
    public ID3D12Resource RenderTarget { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap rtvHeap;
    readonly CpuDescriptorHandle rtvHandle;

    int colorSrvIndex = -1;
    public CpuDescriptorHandle ColorSrvCpu => Dx12Backend.SrvStore.Cpu(colorSrvIndex);
    readonly ID3D12Resource depthTarget;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly CpuDescriptorHandle dsvHandle;
    public bool HasDepth => depthTarget != null;

    int depthSrvIndex = -1;
    public CpuDescriptorHandle DepthSrvCpu => Dx12Backend.SrvStore.Cpu(depthSrvIndex);
    public ID3D12Resource DepthResource => depthTarget;
    ResourceStates depthState = ResourceStates.DepthWrite;
    ResourceStates state = ResourceStates.RenderTarget;

    public ID3D12Heap PlacedHeap { get; }
    public ulong PlacedOffset { get; }
    public bool IsPlaced => PlacedHeap != null;

    public Dx12OffscreenTarget(Dx12Device device, int width, int height, bool withDepth = false,
        Format? colorFormat = null, bool colorReadable = false, bool allowUav = false,
        ID3D12Heap placedHeap = null, ulong placedOffset = 0) {
        dev = device;
        Width = width;
        Height = height;
        Format = colorFormat ?? ColorFormat;

        var rtDesc = ResourceDescription.Texture2D(Format, (uint)width, (uint)height,
            mipLevels: 1, arraySize: 1);
        rtDesc.Flags = ResourceFlags.AllowRenderTarget | (allowUav ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None);
        var clearVal = new ClearValue(Format, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        if (placedHeap != null) {
            PlacedHeap = placedHeap;
            PlacedOffset = placedOffset;
            ClearValue? cv = clearVal;
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

    public void Clear(float r, float g, float b, float a = 1f) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            BindTargets(cl);
            cl.ClearRenderTargetView(rtvHandle, new Vortice.Mathematics.Color4(r, g, b, a));
            if (HasDepth)
                cl.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);
        });
    }

    public void RenderInto(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            BindTargets(cl);
            record(cl);
        });
    }

    public void RenderColorOnly(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            cl.OMSetRenderTargets(rtvHandle);
            record(cl);
        });
    }

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

    public void RenderColorWithExternalDepth(CpuDescriptorHandle dsv, Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            cl.OMSetRenderTargets(new[] { rtvHandle }, dsv);
            record(cl);
        });
    }

    public void CopyColorFrom(Dx12OffscreenTarget src) {
        dev.ExecuteSync(cl => {
            TransitionTo(cl, ResourceStates.CopyDest);
            src.TransitionTo(cl, ResourceStates.CopySource);
            cl.CopyResource(RenderTarget, src.RenderTarget);
            TransitionTo(cl, ResourceStates.RenderTarget);
            src.TransitionTo(cl, ResourceStates.RenderTarget);
        });
    }

    public void CopyColorFromInList(ID3D12GraphicsCommandList4 cl, Dx12OffscreenTarget src) {
        TransitionTo(cl, ResourceStates.CopyDest);
        src.TransitionTo(cl, ResourceStates.CopySource);
        cl.CopyResource(RenderTarget, src.RenderTarget);
        TransitionTo(cl, ResourceStates.NonPixelShaderResource);
        src.TransitionTo(cl, ResourceStates.NonPixelShaderResource);
    }

    public void ColorToShaderResource() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.PixelShaderResource));
    }
    public void ColorToRenderTarget() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.RenderTarget));
    }

    public void ColorToNonPixelShaderResource() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.NonPixelShaderResource));
    }

    public void ColorToUnorderedAccess() {
        dev.ExecuteSync(cl => TransitionTo(cl, ResourceStates.UnorderedAccess));
    }

    public void ColorTransitionInList(ID3D12GraphicsCommandList4 cl, ResourceStates target) => TransitionTo(cl, target);

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

    public void DiscardForAlias(ID3D12GraphicsCommandList4 cl) {
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

    public unsafe void SaveBmp(string path) {
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

        dev.ExecuteSyncImmediate(cl => {
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
        dev.ExecuteSyncImmediate(cl => {
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
        dev.ExecuteSyncImmediate(cl => {
            TransitionTo(cl, ResourceStates.CopyDest);
            cl.CopyTextureRegion(new TextureCopyLocation(RenderTarget, 0), 0, 0, 0,
                new TextureCopyLocation(upload, fp), null);
            TransitionTo(cl, ResourceStates.PixelShaderResource);
        });
    }

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

        dev.ExecuteSyncImmediate(cl => {
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
        int padded = (rowBytes + 3) & ~3;
        int imageSize = padded * h;
        int fileSize = 54 + imageSize;
        var file = new byte[fileSize];

        file[0] = (byte)'B'; file[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(file, 2);
        BitConverter.GetBytes(54).CopyTo(file, 10);
        BitConverter.GetBytes(40).CopyTo(file, 14);
        BitConverter.GetBytes(w).CopyTo(file, 18);
        BitConverter.GetBytes(h).CopyTo(file, 22);
        file[26] = 1;
        file[28] = 24;
        BitConverter.GetBytes(imageSize).CopyTo(file, 34);

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
