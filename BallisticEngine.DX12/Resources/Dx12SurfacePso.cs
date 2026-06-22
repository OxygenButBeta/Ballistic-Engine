using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

public sealed class Dx12SurfacePso {
    public ID3D12PipelineState Pso;
    public bool IsFallback;
    public string Error;
    public string SourcePath;
    public string Source;
    public BallisticEngine.ShaderProperties Props;
}
