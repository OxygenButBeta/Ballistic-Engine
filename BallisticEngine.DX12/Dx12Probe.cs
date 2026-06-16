using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Phase 1 smoke probe: confirms the Vortice DX12 packages resolve and a device can be created. Real
// backend (DX12HDRenderer / DirectXRenderAsset) comes next. Returns a short capability string so the
// host / a headless check can print whether DX12 + DXR are available on this machine.
public static class Dx12Probe {
    public static string Probe() {
        if (!D3D12.IsSupported(Vortice.Direct3D.FeatureLevel.Level_12_0))
            return "DX12: NOT supported (no FL12.0 device)";

        using IDXGIFactory4 factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        // First hardware adapter that can make a FL12.0 device.
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++) {
            AdapterDescription1 desc = adapter.Description1;
            bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
            if (!isSoftware &&
                D3D12.D3D12CreateDevice(adapter, Vortice.Direct3D.FeatureLevel.Level_12_0, out ID3D12Device device).Success) {
                // DXR (ray tracing) tier — the whole point of the migration's back half.
                FeatureDataD3D12Options5 opt5 =
                    device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
                string dxr = opt5.RaytracingTier >= RaytracingTier.Tier1_0
                    ? $"DXR {opt5.RaytracingTier}" : "DXR none";
                string name = desc.Description;
                device.Dispose();
                adapter.Dispose();
                return $"DX12 OK: {name} | {dxr}";
            }
            adapter.Dispose();
        }
        return "DX12: no suitable hardware adapter";
    }
}
