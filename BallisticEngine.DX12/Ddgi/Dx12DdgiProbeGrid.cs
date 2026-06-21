using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;
using BallisticEngine;

namespace BallisticEngine.DX12;

// DDGI probe grid — the world-space irradiance cache (the ONE durable substrate the DDGI pass owns).
//
// A uniform 3D grid of probes covers the scene's world AABB. Each probe stores its diffuse irradiance in an
// octahedral cell of a persistent atlas; a full-res pixel samples the 8 probes around it (trilinear). The
// atlas is cross-frame (a single EMA feedback loop, NEVER pooled) — that is the whole "radiance cache".
//
// Lifecycle: the pass calls Ensure(ctx) each frame. The grid fits the scene AABB once (re-fits only when the
// AABB or the requested grid dimensions change), allocates the irradiance atlas, and exposes the bind handles
// the relight/sample shaders read. View-independent → no reprojection, no screen-space history.
//
// D0: skeleton only — Valid is false, nothing is allocated, the pass is a no-op. D1 fills in the atlas +
// per-probe RT relight; D2 the sample; D3 the visibility atlas (Chebyshev leak fix).
public sealed class Dx12DdgiProbeGrid : IDisposable
{
    readonly Dx12Device dev;

    public Dx12DdgiProbeGrid(Dx12Device device) { dev = device; }

    // ---- grid layout (set on the first successful Ensure; default 16x8x16 = 2048 probes) ----
    public int CountX { get; private set; }
    public int CountY { get; private set; }
    public int CountZ { get; private set; }
    public int ProbeCount => CountX * CountY * CountZ;

    // World-space placement: probe (ix,iy,iz) sits at GridOrigin + (ix,iy,iz)*ProbeSpacing.
    public Vector3 GridOrigin { get; private set; }
    public Vector3 ProbeSpacing { get; private set; }

    // True once the grid is fitted + the irradiance atlas is allocated (D1). D0: always false → pass no-op.
    public bool Valid { get; private set; }

    // D0 stub. D1 fits the grid to the scene AABB + allocates the atlas. Returns Valid.
    public bool Ensure(Dx12FrameContext ctx, int reqX, int reqY, int reqZ)
    {
        // D1 will: compute the scene world AABB from ctx.WholeMeshRenderers, fit GridOrigin/ProbeSpacing,
        // (re)allocate the irradiance atlas when the dims change, and set Valid = true.
        return Valid;
    }

    public void Dispose()
    {
        // D1+: release the atlas resources here.
    }
}
