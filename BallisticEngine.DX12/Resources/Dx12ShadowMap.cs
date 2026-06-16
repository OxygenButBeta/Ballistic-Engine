using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Cascaded sun shadow map: a D32_Float depth TEXTURE ARRAY (one layer per cascade) with a DSV per
// layer (render each cascade's light-space depth) and a single array SRV (sampled with PCF in the
// opaque shader). DX12's equivalent of the GL GLShadowMap depth-array FBO.
public sealed class Dx12ShadowMap : IDisposable {
    public const Format DepthFormat = Format.D32_Float;   // typeless-free path: DSV D32, SRV R32_Float
    public int Size { get; }
    public int Cascades { get; }
    public ID3D12Resource Resource { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly uint dsvInc;
    int srvIndex = -1;
    ResourceStates state;

    public Dx12ShadowMap(Dx12Device device, int size, int cascades) {
        dev = device;
        Size = size;
        Cascades = cascades;

        // Typeless so the same resource serves a D32 DSV and an R32_Float SRV.
        var desc = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)size, (uint)size,
            arraySize: (ushort)cascades, mipLevels: 1);
        desc.Flags = ResourceFlags.AllowDepthStencil;
        var clear = new ClearValue(DepthFormat, 1.0f, 0);
        Resource = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.DepthWrite, clear);
        Resource.Name = "SunShadowCascades";
        state = ResourceStates.DepthWrite;

        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.DepthStencilView, (uint)cascades));
        dsvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        for (int c = 0; c < cascades; c++) {
            var dsvDesc = new DepthStencilViewDescription {
                Format = DepthFormat,
                ViewDimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayDepthStencilView {
                    MipSlice = 0, FirstArraySlice = (uint)c, ArraySize = 1,
                },
            };
            dev.Device.CreateDepthStencilView(Resource, dsvDesc, DsvHandle(c));
        }

        // Array SRV (R32_Float) — the opaque shader samples it as a Texture2DArray with manual PCF.
        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = Format.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DArray,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2DArray = new Texture2DArrayShaderResourceView {
                MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = (uint)cascades,
            },
        };
        dev.Device.CreateShaderResourceView(Resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    CpuDescriptorHandle DsvHandle(int cascade) =>
        new(dsvHeap.GetCPUDescriptorHandleForHeapStart(), cascade, dsvInc);

    // Bind + clear one cascade's depth layer and record depth-only draws. `record` issues the draws.
    public void RenderCascade(ID3D12GraphicsCommandList4 cl, int cascade,
        System.Action<ID3D12GraphicsCommandList4> record) {
        TransitionTo(cl, ResourceStates.DepthWrite);
        cl.RSSetViewport(0, 0, Size, Size);
        cl.RSSetScissorRect(Size, Size);
        CpuDescriptorHandle dsv = DsvHandle(cascade);
        // Depth-only: empty render-target span + the DSV.
        cl.OMSetRenderTargets(System.ReadOnlySpan<CpuDescriptorHandle>.Empty, dsv);
        cl.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
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
