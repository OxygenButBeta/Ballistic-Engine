using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public static class Dx12Backend {
    public static Dx12Device Device { get; private set; }

    public static Dx12DescriptorHeap SrvStore { get; private set; }

    public static Dx12DescriptorHeap BindlessHeap { get; private set; }

    public static Dx12DescriptorHeap UiHeap { get; private set; }

    public static int RegisterUi(CpuDescriptorHandle srcSrv) {
        int idx = UiHeap.Allocate();
        RegisterUiAt(idx, srcSrv);
        return idx;
    }

    public static void RegisterUiAt(int slot, CpuDescriptorHandle srcSrv) {
        Device.Device.CopyDescriptorsSimple(1, UiHeap.Cpu(slot), srcSrv,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public static int RegisterBindless(CpuDescriptorHandle srcSrv) {
        int idx = BindlessHeap.Allocate();
        Device.Device.CopyDescriptorsSimple(1, BindlessHeap.Cpu(idx), srcSrv,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        return idx;
    }

    public static void Initialize(Dx12Device device) {
        Device = device;
        Dx12RenderContext.Device = device;
        SrvStore = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4096, shaderVisible: false);
        BindlessHeap = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, Dx12BindlessTail.HeapCapacity, shaderVisible: true);
        UiHeap = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16384, shaderVisible: true);
    }

    public static Format ToDxgi(TextureFormat fmt, TextureType type) {
        bool srgb = type is TextureType.Diffuse or TextureType.Emissive;
        return fmt switch {
            TextureFormat.RGBA8 => srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
            TextureFormat.RGBA32F => Format.R32G32B32A32_Float,
            TextureFormat.BC1 => srgb ? Format.BC1_UNorm_SRgb : Format.BC1_UNorm,
            TextureFormat.BC3 => srgb ? Format.BC3_UNorm_SRgb : Format.BC3_UNorm,
            TextureFormat.BC5 => Format.BC5_UNorm,
            _ => Format.R8G8B8A8_UNorm,
        };
    }

    public static void Shutdown() {
        SrvStore?.Dispose();
        SrvStore = null;
        BindlessHeap?.Dispose();
        BindlessHeap = null;
        UiHeap?.Dispose();
        UiHeap = null;
        Device = null;
    }
}
