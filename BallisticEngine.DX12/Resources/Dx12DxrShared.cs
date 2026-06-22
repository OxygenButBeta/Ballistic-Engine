using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12DxrShared : IDisposable {
    readonly Dx12Device dev;

    bool dxrChecked, dxrAvailable;

    Dx12SceneAS sceneAS;
    ID3D12Device5 device5;
    Dx12RtGeometry rtGeometry;

    public Dx12DxrShared(Dx12Device device) { dev = device; }

    public bool CheckAvailable(string label) {
        if (!dxrChecked) {
            dxrChecked = true;
            dxrAvailable = dev.HasHardwareRayTracing;
            if (!dxrAvailable) Console.WriteLine($"[{label}] DXR unavailable — using {(label == "RTShadows" ? "cascaded shadows" : label == "RTReflections" ? "SSR" : "SSGI")}.");
        }
        return dxrAvailable;
    }

    public ID3D12Device5 Device5 => device5 ??= dev.Device.QueryInterface<ID3D12Device5>();

    public Dx12SceneAS SceneAS => sceneAS ??= new Dx12SceneAS(dev);

    public Dx12RtGeometry RtGeometry => rtGeometry ??= new Dx12RtGeometry(dev);

    public void Dispose() {
        rtGeometry?.Dispose();
        sceneAS?.Dispose();
        device5?.Dispose();
    }
}
