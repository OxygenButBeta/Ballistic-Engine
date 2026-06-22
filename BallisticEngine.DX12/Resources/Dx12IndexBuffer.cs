using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12IndexBuffer : Dx12Buffer<uint> {
    protected override ResourceStates FinalState => ResourceStates.IndexBuffer;
    public Dx12IndexBuffer(RenderContext renderContext) : base(renderContext) { }
}
