namespace BallisticEngine.DX12;

public sealed class Dx12RenderContext : RenderContext {
    internal static Dx12Device Device { get; set; }

    static int nextId = 1;
    public override int UID { get; protected set; } = nextId++;

    public override void Activate() {
        activeRenderContext = this;
    }

    public override void Deactivate() {
        if (activeRenderContext == this)
            activeRenderContext = null;
    }

    public override void Dispose() {
    }
}
