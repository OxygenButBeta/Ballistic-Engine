using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// DX12 has no VAO — vertex layout lives in the PSO and buffers bind at draw time via the command list.
// So a RenderContext here is just a thin carrier for the device every buffer/texture needs to allocate
// resources (the GL context carried the implicit GL state; the DX12 context carries the device handle).
// Activate/Deactivate are no-ops: binding is per-draw on the command list, not context-global state.
//
// The device is injected once by DirectXRenderAsset.Initialize via SetDevice, then every CreateRenderContext
// hands buffers/meshes their device. (Mesh creates ONE context and builds all its buffers under it, so a
// per-mesh context that knows the device is exactly the seam we need.)
public sealed class Dx12RenderContext : RenderContext {
    // The single device for the whole backend. Set once at Initialize; all contexts share it (there is
    // one GPU/queue, unlike GL's per-context state).
    internal static Dx12Device Device { get; set; }

    static int nextId = 1;
    public override int UID { get; protected set; } = nextId++;

    public override void Activate() {
        activeRenderContext = this;   // keep the base's notion of "active" coherent; no GPU state to bind
    }

    public override void Deactivate() {
        if (activeRenderContext == this)
            activeRenderContext = null;
    }

    public override void Dispose() { /* nothing context-owned; resources free themselves */ }
}
