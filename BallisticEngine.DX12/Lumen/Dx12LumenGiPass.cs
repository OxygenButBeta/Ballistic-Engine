using System;

namespace BallisticEngine.DX12;

// Lumen (UE5-style) GI — the future product GI path being ported alongside Aurora. Event = GlobalIllumination
// (500), the SAME slot Aurora occupies (after Transparents, before Fog) — the two are MUTUALLY EXCLUSIVE (only
// one GI pass runs per frame; see the arbitration note below).
//
// FAZ 0 (THIS milestone — scaffold only): wire the subsystem in cleanly with correct door + gating, but produce
// NOTHING visible. Record() builds/updates the Dx12LumenScene substrate (shared TLAS + per-instance meta + dirty
// stamps) and does nothing else — it writes NO GI, so the scene renders with direct lighting + IBL only and GI is
// black. That is CORRECT for FAZ 0. Later phases fill in the real pipeline:
//   - FAZ 1: per-mesh SDF + software ray tracing.
//   - FAZ 2/3: mesh cards + the surface cache (lit, view-independent radiance).
//   - FAZ 6: screen-probe GI — the first phase that actually CONTRIBUTES diffuse indirect; at THAT point the
//     deferred pass must suppress its IBL diffuse ambient when Lumen is active (see the // FAZ 6 marker in
//     Dx12DeferredLightingPass.Record), exactly as it already does for Aurora, to avoid double-counting.
//
// Gated behind BALLISTIC_DX12_LUMEN (env, default off). When armed, Lumen takes PRECEDENCE over Aurora: Aurora's
// WouldRun yields (it checks !Dx12LumenGiPass.Armed(ctx)), so BALLISTIC_DX12_LUMEN=1 disables Aurora and runs the
// (no-op) Lumen pass instead. HW-RT only (same gate as Aurora — no software fallback in FAZ 0).
public sealed class Dx12LumenGiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "Lumen GI";

    readonly Dx12Device dev;
    readonly Dx12LumenScene scene;

    // The Lumen scene substrate (shared TLAS + per-instance meta). Exposed read-only for later phases (e.g. the
    // reflections pass sampling the surface cache, once FAZ 2/3 land); valid only after a successful Ensure.
    public Dx12LumenScene Scene => scene;

    public Dx12LumenGiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        scene = new Dx12LumenScene(device);
        // FAZ 0: no pipelines/atlases to build yet (no GI is written). Resize is a no-op until FAZ 6 owns targets.
    }

    // The product door. FAZ 0 is ENV-ONLY (BALLISTIC_DX12_LUMEN=1) — there is no LumenVolume yet.
    // TODO (later phase): add a LumenVolume (mirroring AuroraVolume) and follow it when the env is unset, just like
    // Aurora's Armed() folds in ctx.PostFX.AuroraEnabled. For now: armed iff the env door is "1".
    static int envDoor = -2;   // -2 unread, -1 unset (off), 0 force-off, 1 force-on
    public static bool Armed(Dx12FrameContext ctx)
    {
        if (envDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        // FAZ 0: env-only. No volume fallback yet → unset (-1) and force-off (0) both mean OFF.
        return envDoor == 1;
    }

    // The frame-level "Lumen runs" predicate, shared with the orchestrator (mirrored into ctx.LumenActiveThisFrame
    // in BeginRender). Same hard gates as Aurora — HW-RT only, valid TLAS, not the Minimal door. Lumen takes
    // precedence over Aurora purely via Armed: when Lumen is armed, Aurora.WouldRun returns false (it checks
    // !Dx12LumenGiPass.Armed), so only one GI pass is Enabled per frame.
    public static bool WouldRun(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && Armed(ctx) && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    // FAZ -1d-FINAL — when render-graph v2 owns the whole frame (v1 bypassed) it drives the GI slot itself; the v1
    // graph then SKIPS this pass via RgV2OwnsLumenGi. Gate ONLY the instance Enabled, NOT the static WouldRun (read
    // elsewhere to mirror ctx.LumenActiveThisFrame), exactly like Aurora. Door off (and door-on-while-plumbing) =>
    // the flag is false => Enabled == WouldRun, unchanged. See Dx12FrameContext.RgV2OwnsLumenGi.
    public bool Enabled(Dx12FrameContext ctx) => WouldRun(ctx) && !ctx.RgV2OwnsLumenGi;

    // FAZ -1d-FINAL — render-graph v2 entry point. FAZ 0 Record only builds/refreshes the Lumen scene substrate and
    // writes NO GI (scene color is untouched), so RecordV2 needs NO input-state forcing — there is nothing it reads
    // that the v2 import barriers must satisfy. Just run the same body. When FAZ 6 adds real screen-probe GI output
    // (reading G-buffer depth/normal + writing scene color), force those entry states here, mirroring
    // Dx12AuroraGiPass.RecordV2.
    public void RecordV2(Dx12FrameContext ctx) => Record(ctx);

    public void Record(Dx12FrameContext ctx)
    {
        // FAZ 0: build/refresh the scene substrate ONLY. No GI is traced or combined → scene color is untouched,
        // GI stays black. The first armed frame logs the substrate counts (Dx12LumenScene.Ensure logs once per
        // stamp). Later phases trace SDF/screen probes here and additively combine indirect into the HDR color.
        if (!scene.Ensure(ctx))
            return;   // no valid scene AS → nothing to build (Lumen is HW-RT only in FAZ 0; no software fallback)

        // TODO FAZ 1+: SDF trace → FAZ 3: surface-cache gather → FAZ 6: screen-probe diffuse + additive combine.
    }

    public void Dispose()
    {
        scene?.Dispose();
    }
}
