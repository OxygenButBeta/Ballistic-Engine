using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12GBuffer : IDisposable {
    public const int RtCount = 5;
    public const int ShadedRtCount = 4;

    public const int MotionRtIndex = 4;

    static readonly bool Pack = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBUFFER_PACK") == "1";
    public static readonly Format[] ColorFormats = {
        Format.R8G8B8A8_UNorm_SRgb, Pack ? Format.R10G10B10A2_UNorm : Format.R16G16B16A16_Float, Format.R8G8B8A8_UNorm, Pack ? Format.R11G11B10_Float   : Format.R16G16B16A16_Float, Format.R16G16_Float,
    };
    public const Format DepthFormat = Format.D32_Float;

    static Format TypelessOf(Format shaded) => shaded switch {
        Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_Typeless,
        Format.R8G8B8A8_UNorm      => Format.R8G8B8A8_Typeless,
        Format.R16G16B16A16_Float  => Format.R16G16B16A16_Typeless,
        Format.R16G16_Float        => Format.R16G16_Typeless,
        Format.R10G10B10A2_UNorm   => Format.R10G10B10A2_Typeless,
        _ => shaded,
    };

    public static Format UavFormatOf(Format shaded) => shaded switch {
        Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
        _ => shaded,
    };

    public int Width { get; }
    public int Height { get; }

    readonly Dx12Device dev;
    readonly ID3D12Resource[] colors = new ID3D12Resource[RtCount];
    readonly ID3D12DescriptorHeap rtvHeap;
    readonly uint rtvInc;
    readonly int[] colorSrv = new int[RtCount];
    readonly ResourceStates[] colorState = new ResourceStates[RtCount];

    readonly ID3D12Resource depth;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly CpuDescriptorHandle dsvHandle;
    int depthSrv = -1;
    ResourceStates depthState = ResourceStates.DepthWrite;

    public Dx12GBuffer(Dx12Device device, int width, int height) {
        dev = device;
        Width = width;
        Height = height;

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, RtCount));
        rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        CpuDescriptorHandle rtvStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();

        for (int i = 0; i < RtCount; i++) {
            Format shaded = ColorFormats[i];
            Format typeless = TypelessOf(shaded);
            var rtDesc = ResourceDescription.Texture2D(typeless, (uint)width, (uint)height, mipLevels: 1, arraySize: 1);
            rtDesc.Flags = ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess;
            var clearVal = new ClearValue(shaded, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            colors[i] = dev.Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None, rtDesc,
                ResourceStates.RenderTarget, clearVal);
            colors[i].Name = $"GBuffer{i}";
            colorState[i] = ResourceStates.RenderTarget;
            dev.Device.CreateRenderTargetView(colors[i], new RenderTargetViewDescription {
                Format = shaded, ViewDimension = RenderTargetViewDimension.Texture2D,
            }, RtvHandle(i));

            colorSrv[i] = Dx12Backend.SrvStore.Allocate();
            dev.Device.CreateShaderResourceView(colors[i], new ShaderResourceViewDescription {
                Format = shaded,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            }, Dx12Backend.SrvStore.Cpu(colorSrv[i]));
        }

        var dDesc = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)width, (uint)height,
            mipLevels: 1, arraySize: 1);
        dDesc.Flags = ResourceFlags.AllowDepthStencil;
        var dClear = new ClearValue(DepthFormat, 1.0f, 0);
        depth = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, dDesc,
            ResourceStates.DepthWrite, dClear);
        depth.Name = "GBufferDepth";
        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.DepthStencilView, 1));
        dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateDepthStencilView(depth, new DepthStencilViewDescription {
            Format = DepthFormat, ViewDimension = DepthStencilViewDimension.Texture2D,
        }, dsvHandle);

        depthSrv = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(depth, new ShaderResourceViewDescription {
            Format = Format.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(depthSrv));
    }

    public CpuDescriptorHandle ColorSrvCpu(int i) => Dx12Backend.SrvStore.Cpu(colorSrv[i]);
    public CpuDescriptorHandle DepthSrvCpu => Dx12Backend.SrvStore.Cpu(depthSrv);
    public CpuDescriptorHandle DsvHandle => dsvHandle;
    public ID3D12Resource DepthResource => depth;
    public ID3D12Resource MotionResource => colors[MotionRtIndex];

    CpuDescriptorHandle RtvHandle(int i) => new(rtvHeap.GetCPUDescriptorHandleForHeapStart(), i, rtvInc);

    public void RenderGeometry(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ResourceStates.RenderTarget);
            DepthTransition(cl, ResourceStates.DepthWrite);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[RtCount];
            for (int i = 0; i < RtCount; i++) {
                rtvs[i] = RtvHandle(i);
                cl.ClearRenderTargetView(rtvs[i], new Vortice.Mathematics.Color4(0, 0, 0, 0));
            }
            cl.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);
            cl.OMSetRenderTargets(rtvs, dsvHandle);
            record(cl);
        });
    }

    const ResourceStates ShaderRead = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
    public void ToShaderResource() {
        dev.ExecuteSync(cl => {
            for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ShaderRead);
            DepthTransition(cl, ShaderRead);
        });
    }

    public void DepthToReadOnly() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.DepthRead));
    public void DepthToWrite() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.DepthWrite));
    public void DepthToShaderResource() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.PixelShaderResource));
    public void DepthToNonPixelShaderResource() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.NonPixelShaderResource));

    public unsafe byte[] ReadbackRaw(int which, out int bytesPerPixel) {
        ID3D12Resource res = which < 0 ? depth : colors[which];
        ResourceDescription resDesc = res.Description;
        var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
        dev.Device.GetCopyableFootprints(resDesc, 0, 1, 0, fps, rc, rs, out ulong totalBytes);
        int rowPitch = (int)fps[0].Footprint.RowPitch;
        int rowBytes = (int)rs[0];
        bytesPerPixel = rowBytes / Width;

        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);

        ResourceStates prior = which < 0 ? depthState : colorState[which];
        dev.ExecuteSync(cl => {
            Transition(cl, res, ref prior, ResourceStates.CopySource, which);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fps[0]), 0, 0, 0,
                new TextureCopyLocation(res, 0), null);
        });
        ResourceStates restoreTo = which < 0 ? ResourceStates.NonPixelShaderResource
                                             : (ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        dev.ExecuteSync(cl => Transition(cl, res, ref prior, restoreTo, which));

        var dst = new byte[(long)Width * Height * bytesPerPixel];
        byte* mapped = readback.Map<byte>(0);
        try {
            for (int y = 0; y < Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    (IntPtr)(mapped + (long)y * rowPitch), dst, y * Width * bytesPerPixel, Width * bytesPerPixel);
        } finally { readback.Unmap(0); }
        return dst;
    }

    void Transition(ID3D12GraphicsCommandList4 cl, ID3D12Resource res, ref ResourceStates cur, ResourceStates target, int which) {
        if (cur == target) return;
        cl.ResourceBarrierTransition(res, cur, target);
        cur = target;
        if (which < 0) depthState = target; else colorState[which] = target;
    }

    void ColorTransition(ID3D12GraphicsCommandList4 cl, int i, ResourceStates target) {
        if (colorState[i] == target) return;
        cl.ResourceBarrierTransition(colors[i], colorState[i], target);
        colorState[i] = target;
    }

    public void ColorsToUav(ID3D12GraphicsCommandList4 cl) {
        for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ResourceStates.UnorderedAccess);
    }
    public void CreateColorUav(int i, CpuDescriptorHandle dst) {
        dev.Device.CreateUnorderedAccessView(colors[i], null, new UnorderedAccessViewDescription {
            Format = UavFormatOf(ColorFormats[i]), ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, dst);
    }
    public void ColorsToShaderRead(ID3D12GraphicsCommandList4 cl) {
        for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ShaderRead);
    }

    public void DepthTransitionPublic(ID3D12GraphicsCommandList4 cl, ResourceStates target) => DepthTransition(cl, target);
    void DepthTransition(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (depthState == target) return;
        cl.ResourceBarrierTransition(depth, depthState, target);
        depthState = target;
    }

    public void Dispose() {
        dsvHeap.Dispose();
        depth.Dispose();
        rtvHeap.Dispose();
        for (int i = 0; i < RtCount; i++) colors[i].Dispose();
    }
}
