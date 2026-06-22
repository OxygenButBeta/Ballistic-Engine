namespace BallisticEngine.DX12;

public interface IRenderPass {
    Dx12RenderPassEvent Event { get; }

    string Name { get; }

    bool Enabled(Dx12FrameContext ctx);

    void Resize(int width, int height) { }

    void Record(Dx12FrameContext ctx);

    void Declare(Dx12PassBuilder builder) { }
}
