using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// A render-target cubemap: an RGBA16F TextureCube with an RTV per (face, mip) and a shader-visible-ready
// SRV, used to BAKE an environment (procedural sky) and to hold IBL convolution outputs (irradiance /
// prefiltered specular). DX12's render-to-cube equivalent of the GL FBO-per-face bake. Renders via a
// fullscreen triangle into one face at a time on the shared command list.
//
// The SRV is created in the persistent CPU SRV store (Dx12Backend.SrvStore) so the renderer copies it
// into a shader-visible heap like any texture. Mips are allocated but only written if you render into
// them (prefilter does; the env bake writes mip 0 then can GenerateMips-equivalent by rendering, or the
// caller downsamples). For Phase-1 use we render mip 0 only.
public sealed class Dx12CubeTarget : IDisposable {
    public const Format Fmt = Format.R16G16B16A16_Float;
    public int Resolution { get; }
    public int MipLevels { get; }
    public ID3D12Resource Resource { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap rtvHeap;   // 6*mips RTVs
    readonly uint rtvInc;
    int srvIndex = -1;
    ResourceStates state;

    public Dx12CubeTarget(Dx12Device device, int resolution, int mipLevels = 1) {
        dev = device;
        Resolution = resolution;
        MipLevels = System.Math.Max(1, mipLevels);

        var desc = ResourceDescription.Texture2D(Fmt, (uint)resolution, (uint)resolution,
            arraySize: 6, mipLevels: (ushort)MipLevels);
        desc.Flags = ResourceFlags.AllowRenderTarget;
        Resource = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.PixelShaderResource);
        Resource.Name = $"CubeTarget{resolution}";
        state = ResourceStates.PixelShaderResource;

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, (uint)(6 * MipLevels)));
        rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        CpuDescriptorHandle rtvStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int mip = 0; mip < MipLevels; mip++) {
            for (int face = 0; face < 6; face++) {
                var rtvDesc = new RenderTargetViewDescription {
                    Format = Fmt,
                    ViewDimension = RenderTargetViewDimension.Texture2DArray,
                    Texture2DArray = new Texture2DArrayRenderTargetView {
                        MipSlice = (uint)mip, FirstArraySlice = (uint)face, ArraySize = 1, PlaneSlice = 0,
                    },
                };
                dev.Device.CreateRenderTargetView(Resource, rtvDesc, RtvHandle(mip, face));
            }
        }

        // Persistent cube SRV (full mip chain).
        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = Fmt,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.TextureCube,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            TextureCube = new TextureCubeShaderResourceView { MipLevels = (uint)MipLevels, MostDetailedMip = 0 },
        };
        dev.Device.CreateShaderResourceView(Resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    CpuDescriptorHandle RtvHandle(int mip, int face) =>
        new(rtvHeap.GetCPUDescriptorHandleForHeapStart(), mip * 6 + face, rtvInc);

    // Record a render into one (face, mip): transitions to RenderTarget, binds the face RTV + a viewport
    // sized to that mip, runs `record`, leaves the cube in RenderTarget (call ToShaderResource after the
    // last face). `record` issues the fullscreen-triangle draw.
    public void RenderFace(ID3D12GraphicsCommandList4 cl, int face, int mip,
        System.Action<ID3D12GraphicsCommandList4> record) {
        TransitionTo(cl, ResourceStates.RenderTarget);
        int mipRes = System.Math.Max(1, Resolution >> mip);
        cl.RSSetViewport(0, 0, mipRes, mipRes);
        cl.RSSetScissorRect(mipRes, mipRes);
        CpuDescriptorHandle rtv = RtvHandle(mip, face);
        cl.OMSetRenderTargets(rtv);
        record(cl);
    }

    public void ToShaderResource(ID3D12GraphicsCommandList4 cl) =>
        TransitionTo(cl, ResourceStates.PixelShaderResource);

    void TransitionTo(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (state == target) return;
        cl.ResourceBarrierTransition(Resource, state, target);
        state = target;
    }

    public void Dispose() {
        rtvHeap.Dispose();
        Resource.Dispose();
    }
}
