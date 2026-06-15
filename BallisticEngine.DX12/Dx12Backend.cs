using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Process-wide handles the DX12 resource classes (textures, buffers) need but can't get through the
// engine's API-agnostic factory signatures. Set once by DirectXRenderAsset.Initialize. Mirrors how the
// GL backend relied on the single implicit GL context — here it's an explicit device + the CPU-side
// descriptor heap textures park their persistent SRVs in.
//
// Single-device, single-thread-of-render assumption (the headless screenshot path and the editor's one
// render thread both satisfy it). Not thread-safe by design — DX12 command recording is serialized here.
public static class Dx12Backend {
    public static Dx12Device Device { get; private set; }

    // CPU-only (non-shader-visible) SRV heap: every Texture2D/Texture3D allocates ONE persistent
    // descriptor here at upload. The renderer copies these into a shader-visible heap per draw.
    public static Dx12DescriptorHeap SrvStore { get; private set; }

    public static void Initialize(Dx12Device device) {
        Device = device;
        Dx12RenderContext.Device = device;
        // 4096 persistent texture descriptors is plenty for a scene's material set (SunTemple/Bistro
        // have a few hundred unique maps). Grow if a scene ever needs more.
        SrvStore = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4096, shaderVisible: false);
    }

    // Map the engine's TextureFormat to a DXGI format. sRGB color maps (Diffuse/Emissive) use the
    // _SRGB variants so the GPU linearizes on sample — matching the GL backend's sRGB texture handling
    // so albedo isn't washed/over-bright. Data maps (normal/roughness/metallic/AO) stay UNORM (linear).
    public static Format ToDxgi(TextureFormat fmt, TextureType type) {
        bool srgb = type is TextureType.Diffuse or TextureType.Emissive;
        return fmt switch {
            TextureFormat.RGBA8 => srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
            TextureFormat.RGBA32F => Format.R32G32B32A32_Float,
            TextureFormat.BC1 => srgb ? Format.BC1_UNorm_SRgb : Format.BC1_UNorm,
            TextureFormat.BC3 => srgb ? Format.BC3_UNorm_SRgb : Format.BC3_UNorm,
            TextureFormat.BC5 => Format.BC5_UNorm,   // two-channel normal data, never sRGB
            _ => Format.R8G8B8A8_UNorm,
        };
    }

    public static void Shutdown() {
        SrvStore?.Dispose();
        SrvStore = null;
        Device = null;
    }
}
