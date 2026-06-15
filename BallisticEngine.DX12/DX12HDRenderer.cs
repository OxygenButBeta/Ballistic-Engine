using BallisticEngine.DX12;
using BallisticEngine.Rendering;   // BatchGroup<T>
using GLMatrix4 = OpenTK.Mathematics.Matrix4;

namespace BallisticEngine;

// The DX12 forward renderer. Built up incrementally per Docs/Plans/dx-native-abstraction-redesign.md:
// this is the minimal compiling shell (device + offscreen color/depth target + the abstract surface the
// engine/editor depend on). The real opaque draw loop (interleaved mesh buffers, per-frame material
// descriptor table, depth prepass + opaque + sky) lands in the later steps of that plan, built against
// the DX-native abstraction rather than bridged onto the GL-shaped one.
//
// Drives shading via constant buffers + descriptor tables (NOT a GL per-name uniform API), and uses NO
// reflection on the per-frame path (standing rule).
public sealed class DX12HDRenderer : HDRenderer {
    readonly Dx12Device dev;
    Dx12OffscreenTarget target;
    int targetW = 1920, targetH = 1080;

    public DX12HDRenderer(Dx12Device device) {
        dev = device;
    }

    // No editor display wiring yet (Phase 7) — headless path only. None = nothing to ImGui::Image.
    public override RenderHandle SceneColorHandle => RenderHandle.None;
    public override RenderHandle GameColorHandle => RenderHandle.None;

    public override void ResizeSceneTarget(int width, int height) => Resize(width, height);
    public override void ResizeGameTarget(int width, int height) => Resize(width, height);

    void Resize(int width, int height) {
        if (width <= 0 || height <= 0) return;
        if (target != null && width == targetW && height == targetH) return;
        targetW = width; targetH = height;
        target?.Dispose();
        target = new Dx12OffscreenTarget(dev, width, height, withDepth: true);
    }

    public override void Initialize() {
        target = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: true);
    }

    public override RenderMetrics BeginRender(RendererArgs args) {
        // Minimal: clear to the scene background so the harness produces a valid frame while the real
        // draw loop is built up. Real opaque rendering arrives in the plan's later steps.
        target?.Clear(0.02f, 0.02f, 0.03f);
        return default;
    }

    public override void PostRenderCleanUp() { }

    // DX12 readback to BMP — the headless screenshot path (the GL window host's glReadPixels equivalent).
    public void SaveFrame(string path) => target?.SaveBmp(path);
    public int Width => targetW;
    public int Height => targetH;

    // Internal pipeline steps — no engine/editor caller (the renderer drives opaques itself in BeginRender).
    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets,
        RendererArgs args, bool isShadowPass) { }
    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args) { }
    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args) { }
    public override void RenderInstancing(Mesh mesh, Material material, GLMatrix4[] transforms, RendererArgs args) { }
}
