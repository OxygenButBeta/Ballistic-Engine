using System;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// Shared DXR substrate used by THREE consumers — RT sun shadows (still inline core in the orchestrator),
// the GI pass (Dx12GiPass, RT-GI branch), and the Reflections pass (Dx12ReflectionsPass, RT-reflections
// branch). Before chunk 10 these lived as four scattered DX12HDRenderer fields (sceneAS / device5 /
// dxrChecked+dxrAvailable / rtGeometry) that EnsureRtShadows / EnsureRtGi / EnsureRtReflections each lazily
// created on a first-come basis. When GI + Reflections move into passes, that shared state can no longer be
// a renderer field reachable by all three — so it's collected into THIS holder, which the orchestrator
// creates once and threads through Dx12FrameContext (Dxr). RT shadows still references it inline; the two
// passes reference it via ctx. The lazy-create-on-first-use semantics are PRESERVED verbatim: whichever
// effect runs first builds sceneAS/device5/rtGeometry, the rest reuse them (a static scene builds the AS
// once). This is an ORCHESTRATION holder only — no Lumen/DXR algorithm lives here (hard constraint).
public sealed class Dx12DxrShared : IDisposable {
    readonly Dx12Device dev;

    // The DXR capability check: done once (lazy), eager device-wide flag. dxrAvailable is FORCE_NORT-aware
    // (Dx12Device.HasHardwareRayTracing already honours BALLISTIC_DX12_FORCE_NORT). The three Ensure* methods
    // each called this same check before; CheckAvailable centralises it so the result is identical no matter
    // which effect probes first. `label` is the per-effect log tag the old inline check printed.
    bool dxrChecked, dxrAvailable;

    // Lazily created shared objects. sceneAS = the scene BLAS/TLAS (built by the first RT effect, reused by
    // the rest; cached by a geometry stamp). device5 = the ID3D12Device5 facet used to CreateStateObject for
    // each RT PSO. rtGeometry = the per-instance bindless geo/material SRVs (RT-GI + RT-reflections only; RT
    // shadows never touches it). All three keep the EXACT lazy lifetimes the inline Ensure* methods had.
    Dx12SceneAS sceneAS;
    ID3D12Device5 device5;
    Dx12RtGeometry rtGeometry;

    // The DDGI world-probe radiance cache (BALLISTIC_DX12_DDGI=1). Inline it was a DX12HDRenderer field shared
    // between DrawRtGi (which CREATES + updates it) and DrawRtReflections (which READS its atlas/grid/ProbeState
    // as the hit ambient). With GI + Reflections in separate passes that shared field moves HERE: the GI pass
    // creates it lazily in DrawRtGi (Ddgi = new Dx12Ddgi(dev) the first DDGI frame) and the Reflections pass
    // reads it via ctx.Dxr.Ddgi. Settable so the GI pass can assign the instance it creates; null until then.
    public Dx12Ddgi Ddgi { get; set; }

    public Dx12DxrShared(Dx12Device device) { dev = device; }

    // The one-time DXR-availability probe (verbatim from the inline Ensure* methods, with the per-effect log
    // tag). Returns false when DXR is unavailable → the caller falls back (cascades / SSGI / SSR). Idempotent.
    public bool CheckAvailable(string label) {
        if (!dxrChecked) {
            dxrChecked = true;
            dxrAvailable = dev.HasHardwareRayTracing;   // eager device-wide flag (FORCE_NORT-aware)
            if (!dxrAvailable) Console.WriteLine($"[{label}] DXR unavailable — using {(label == "RTShadows" ? "cascaded shadows" : label == "RTReflections" ? "SSR" : "SSGI")}.");
        }
        return dxrAvailable;
    }

    // Lazily get the ID3D12Device5 facet (created once, shared). Matches `if (device5 == null) device5 =
    // dev.Device.QueryInterface<ID3D12Device5>();` from each Ensure*.
    public ID3D12Device5 Device5 => device5 ??= dev.Device.QueryInterface<ID3D12Device5>();

    // Lazily get the shared scene AS (created once by the first RT effect). Matches `if (sceneAS == null)
    // sceneAS = new Dx12SceneAS(dev);` from each Ensure*.
    public Dx12SceneAS SceneAS => sceneAS ??= new Dx12SceneAS(dev);

    // The per-instance bindless geometry/material SRVs. RT-GI's EnsureRtGi built it as `rtGeometry = new
    // Dx12RtGeometry(dev)`; RT-reflections' EnsureRtReflections used `rtGeometry ??= new Dx12RtGeometry(dev)`
    // (null-coalescing — reuse if GI already built it). Use `??=` so whichever RT effect builds first wins and
    // the other reuses it (identical to the inline order: GI built non-coalescing, reflections coalescing —
    // but GI always ran first in practice; ??= is the safe superset and matches reflections' intent).
    public Dx12RtGeometry RtGeometry => rtGeometry ??= new Dx12RtGeometry(dev);

    public void Dispose() {
        Ddgi?.Dispose();
        rtGeometry?.Dispose();
        sceneAS?.Dispose();
        device5?.Dispose();
    }
}
