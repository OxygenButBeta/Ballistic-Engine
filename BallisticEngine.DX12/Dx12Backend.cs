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

    // LARGE shader-visible CBV_SRV_UAV heap for SM6.6 BINDLESS (ResourceDescriptorHeap[idx] in HLSL). The
    // GPU-driven path mirrors each material texture's persistent SrvStore descriptor into here once; the
    // material's stored index lets a single ExecuteIndirect draw submeshes with DIFFERENT materials (no
    // per-draw descriptor-table rebinding). Bound via SetDescriptorHeaps for the GPU-driven geometry pass;
    // the root sig must set the ...HeapDirectlyIndexed flag. Bump-allocated, Reset on a material rebuild.
    public static Dx12DescriptorHeap BindlessHeap { get; private set; }

    // SHADER-VISIBLE heap dedicated to the EDITOR's ImGui present pass: the final scene/game color
    // (DX12HDRenderer.ldr) SRV, the ImGui font atlas, and asset thumbnails/previews all live here so a
    // SINGLE SetDescriptorHeaps(UiHeap) covers every ImGui draw and ImTextureID == a GPU descriptor ptr
    // INTO this heap. Kept separate from BindlessHeap (which the offscreen GPU-driven passes bind) so the
    // two never interfere — the ImGui pass runs on its own command list after the scene is done. Null in
    // the headless/runtime path (no editor present); only the editor's swapchain host binds it.
    public static Dx12DescriptorHeap UiHeap { get; private set; }

    // Mirror a CPU-only SRV into the shader-visible UI heap; returns the slot index. The caller turns the
    // slot into a GPU handle via UiHeap.Gpu(index) and feeds that ptr to ImGui as an ImTextureID. Re-copy
    // into the SAME slot (RegisterUiAt) on resize to keep a stable handle without leaking slots.
    public static int RegisterUi(CpuDescriptorHandle srcSrv) {
        int idx = UiHeap.Allocate();
        RegisterUiAt(idx, srcSrv);
        return idx;
    }

    // Re-point an existing UI-heap slot at a (possibly recreated) source SRV — used when a resize rebuilds
    // the underlying texture but the caller wants the GPU handle (ImTextureID) to stay constant.
    public static void RegisterUiAt(int slot, CpuDescriptorHandle srcSrv) {
        Device.Device.CopyDescriptorsSimple(1, UiHeap.Cpu(slot), srcSrv,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    // Mirror a texture's persistent (CPU-only) SRV into the shader-visible bindless heap; returns the
    // bindless index the shader uses as ResourceDescriptorHeap[index]. Caller caches by texture.
    public static int RegisterBindless(CpuDescriptorHandle srcSrv) {
        int idx = BindlessHeap.Allocate();
        Device.Device.CopyDescriptorsSimple(1, BindlessHeap.Cpu(idx), srcSrv,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        return idx;
    }

    public static void Initialize(Dx12Device device) {
        Device = device;
        Dx12RenderContext.Device = device;
        // 4096 persistent texture descriptors is plenty for a scene's material set (SunTemple/Bistro
        // have a few hundred unique maps). Grow if a scene ever needs more.
        SrvStore = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4096, shaderVisible: false);
        // Shader-visible bindless table: a few thousand texture descriptors (whole-mesh scenes use a few
        // hundred unique maps × 6 slots). Bump-allocated; Reset() + re-register on a material-set change.
        BindlessHeap = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16384, shaderVisible: true);
        // Editor ImGui present heap (scene/game color + font atlas + thumbnails). Generous so a busy asset
        // browser's thumbnails all fit; only the editor swapchain host populates it (null cost headless).
        UiHeap = new Dx12DescriptorHeap(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16384, shaderVisible: true);
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
        BindlessHeap?.Dispose();
        BindlessHeap = null;
        UiHeap?.Dispose();
        UiHeap = null;
        Device = null;
    }
}
