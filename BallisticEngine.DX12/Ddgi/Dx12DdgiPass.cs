using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DDGI — the single product-facing GI pass (event GlobalIllumination = 500, the slot the legacy Lumen pass
// held). World-space irradiance probe grid; the design replaces Lumen V2 with ONE predictable feedback loop:
//
//   1. Relight  (compute)  per-probe RT trace → shade hits (sun+shadow-ray + punctual + emissive) + sky on a
//                          miss → integrate into the probe's octahedral irradiance cell, EMA-blended over the
//                          previous frame. View-independent: no reprojection, no motion vectors.
//   2. Sample   (compute)  per full-res pixel: gather the 8 probes around it (trilinear), → indirect E.
//   3. Combine  (PS)       E*albedo*ao/PI added into the HDR color (One/One). The deferred pass already
//                          suppressed its IBL diffuse ambient (ctx.GiActiveThisFrame) so no double count.
//
// No screen-space temporal / SVGF / async double-buffer / per-pixel trace — the entire ghosting/disocclusion
// class is gone because the cache lives in world space. Gated behind the GlobalIllumination volume +
// BALLISTIC_DX12_DDGI; HW-RT only (no hidden SSGI fallback). Default-off = no-op, byte-identical no-GI frame.
//
// D0: skeleton — grid is never Valid, Record is a no-op. D1 adds the probe grid + relight; D2 sample+combine.
public sealed class Dx12DdgiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "DDGI";

    readonly Dx12Device dev;
    readonly Dx12DdgiProbeGrid grid;

    // The probe grid the Reflections pass (event 600) will sample for rough reflections (D5). Exposed read-only;
    // valid only after a successful Ensure this frame (reflections also gates on ctx.GiActiveThisFrame).
    public Dx12DdgiProbeGrid Grid => grid;

    public Dx12DdgiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        grid = new Dx12DdgiProbeGrid(device);
        Resize(width, height);
    }

    // The product door. DDGI is driven by the GlobalIllumination VOLUME (ctx.PostFX.LumenEnabled, default ON —
    // the volume/asset field name is kept as "Lumen*" for serialization compatibility). The BALLISTIC_DX12_DDGI
    // env door overrides for A/B: "1" forces on, "0" forces off, unset → follow the volume.
    static int envDoor = -2;   // -2 unread, -1 unset(follow volume), 0 force-off, 1 force-on
    static bool Armed(Dx12FrameContext ctx)
    {
        if (envDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        return envDoor == 1 || (envDoor == -1 && ctx.PostFX.LumenEnabled);
    }

    // The frame-level "GI runs" predicate, shared with the orchestrator (which mirrors it into
    // ctx.GiActiveThisFrame so the deferred pass suppresses its IBL diffuse ambient before this pass adds its
    // own diffuse indirect). HW-RT only — no hidden screen-space fallback.
    public static bool WouldRun(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && Armed(ctx) && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    public bool Enabled(Dx12FrameContext ctx) => WouldRun(ctx);

    // Requested grid dimensions (BALLISTIC_DX12_DDGI_GRID="X x Y x Z", default 16x8x16). Read once.
    static int gridX = -1, gridY, gridZ;
    static void ReadGrid()
    {
        if (gridX >= 0) return;
        gridX = 16; gridY = 8; gridZ = 16;
        string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_GRID");
        if (!string.IsNullOrEmpty(v))
        {
            string[] p = v.Split('x', 'X', '*', ',');
            if (p.Length == 3 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) && int.TryParse(p[2], out int z)
                && x > 0 && y > 0 && z > 0)
            { gridX = x; gridY = y; gridZ = z; }
        }
    }

    public void Resize(int width, int height)
    {
        // D2+: the full-res `indirect` E target + sample/combine descriptor heaps resize here.
    }

    public void Record(Dx12FrameContext ctx)
    {
        ReadGrid();
        // Fit/allocate the probe grid against this frame's scene. D0: Ensure is a stub → Valid stays false → no-op.
        if (!grid.Ensure(ctx, gridX, gridY, gridZ))
            return;

        // D1: relight → D2: sample → combine. Nothing yet.
    }

    public void Dispose()
    {
        grid.Dispose();
    }
}
