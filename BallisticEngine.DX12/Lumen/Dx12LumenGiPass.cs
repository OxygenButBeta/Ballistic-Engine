using System;
using BallisticEngine;          // IStaticMeshRenderer
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// Lumen V2 — the single product-facing GI pass (plan §Target Shape: one `Lumen` path, screen traces first,
// hardware RT for off-screen hits, surface/radiance cache for stable indirect). Event = GlobalIllumination
// (500), the slot the legacy GI pass occupied (after Transparents, before Fog).
//
// This pass owns the Lumen scene substrate (Dx12LumenScene) and, across the milestones, the screen-trace +
// HW-RT pipelines, the radiance cache, and the final gather. It writes ONE clean diffuse-indirect buffer the
// deferred/compose path adds without double-counting IBL.
//
// P1 (THIS commit): substrate only. Record ensures Dx12LumenScene (shared TLAS + bindless geometry + card
// table + atlases) and logs object/card/atlas/dirty counts. NO shading, NO indirect buffer yet → byte-
// identical to a no-Lumen frame. Gated behind BALLISTIC_DX12_LUMEN=1 (product `GiMode` volume comes later);
// default-off means the substrate never allocates and this pass is a no-op.
public sealed class Dx12LumenGiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "Lumen GI";

    readonly Dx12Device dev;
    readonly Dx12LumenScene scene;

    public Dx12LumenGiPass(Dx12Device device)
    {
        dev = device;
        scene = new Dx12LumenScene(device);
    }

    // The product door. BALLISTIC_DX12_LUMEN=1 arms Lumen; unset/0 = off (no substrate alloc, no-op Record).
    // Resolved once (no per-frame env churn). A `GiMode` volume parameter will drive this once the path ships.
    int? armed;
    bool Armed => (armed ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN") == "1" ? 1 : 0) == 1;

    public bool Enabled(Dx12FrameContext ctx)
    {
        if (ctx.Doors.Minimal) return false;
        if (!Armed) return false;
        // Lumen is HW-RT only (plan gate #6: no hidden fallback to SSGI). Without ray tracing the pass is
        // simply unavailable — the scene Ensure also re-checks, but gate here so we don't even Record.
        return ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;
    }

    public void Record(Dx12FrameContext ctx)
    {
        // P1: build/refresh the substrate and log its counts. No shading. The returned validity drives nothing
        // yet (P2 gates the screen-trace + RT dispatch on it).
        scene.Ensure(ctx);
    }

    public void Dispose() => scene.Dispose();
}
