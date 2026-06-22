namespace BallisticEngine.DX12;

public sealed class Dx12CullProbePass : IRenderPass {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.BeforeShadows;
    public string Name => "CullProbe";

    public bool Enabled(Dx12FrameContext ctx) => false;

    public void Record(Dx12FrameContext ctx) {
    }

    public void Declare(Dx12PassBuilder b) {
        b.AllowCulling();
        b.Write(b.Resource("CullProbeScratch", imported: false));
    }
}
