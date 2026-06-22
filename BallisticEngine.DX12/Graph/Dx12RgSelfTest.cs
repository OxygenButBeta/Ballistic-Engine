using Vortice.DXGI;

namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2 self-test. CPU-only: exercises Reset/AddPass/Compile (DAG, cull,
// lifetimes, aliasing math) and prints the compile report. Realises the heap + placed transients
// on the real device but records NO GPU work (no Execute), so it is a safe init-time smoke test.
//
// Trigger: BALLISTIC_DX12_RG_SELFTEST=1 (called from a renderer init site later; standalone here).

public static class Dx12RgSelfTest {
    public static string Run(Dx12Device dev) {
        var g = new Dx12RgGraph(dev);
        g.Reset();

        // depth (write) -> gbuffer (write, reads depth) -> lighting (UAV, reads gbuffer) ->
        // a dangling pass whose output nobody reads (must be CULLED) -> a NeverCull side-effect pass.
        Dx12RgHandle depth = default, gbuf = default, lit = default, dangling = default;

        g.AddPass("Depth", b => {
            depth = b.CreateTransient(Dx12RgResourceDesc.Texture2D("rg.depth", 1920, 1080,
                Format.D32_Float, Dx12RgResourceFlags.AllowDepthStencil,
                clear: Dx12RgClearValue.DepthStencil(Format.D32_Float, 1f, 0)));
            b.Write(depth, Dx12RgResourceState.DepthWrite);
        }, _ => { });

        g.AddPass("GBuffer", b => {
            gbuf = b.CreateTransient(Dx12RgResourceDesc.Texture2D("rg.gbuffer", 1920, 1080,
                Format.R16G16B16A16_Float, Dx12RgResourceFlags.AllowRenderTarget));
            b.Read(depth, Dx12RgResourceState.DepthRead);
            b.Write(gbuf, Dx12RgResourceState.RenderTarget);
        }, _ => { });

        g.AddPass("Lighting", Dx12RgQueue.AsyncCompute, b => {
            lit = b.CreateTransient(Dx12RgResourceDesc.Texture2D("rg.lit", 1920, 1080,
                Format.R16G16B16A16_Float, Dx12RgResourceFlags.AllowUnorderedAccess));
            b.Read(gbuf, Dx12RgResourceState.NonPixelShaderRead);
            b.Write(lit, Dx12RgResourceState.UnorderedAccess);
        }, _ => { });

        g.AddPass("DeadPass", b => {
            dangling = b.CreateTransient(Dx12RgResourceDesc.Texture2D("rg.dead", 512, 512,
                Format.R8G8B8A8_UNorm, Dx12RgResourceFlags.AllowRenderTarget));
            b.Write(dangling, Dx12RgResourceState.RenderTarget); // nobody reads rg.dead -> culled
        }, _ => { });

        // 'lit' must survive: mark its consumer present via a NeverCull readback pass.
        g.AddPass("Present", b => {
            b.Read(lit, Dx12RgResourceState.CopySrc);
            b.NeverCull();
        }, _ => { });

        g.Compile();
        return g.LastCompileReport;
    }
}
