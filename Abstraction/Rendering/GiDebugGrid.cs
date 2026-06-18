namespace BallisticEngine;

// CPU mirror of the DDGI world-radiance-cache probe grid, for EDITOR GIZMOS only (the GI Debug
// visualisers on the Volume component). The authoritative grid lives in the DX12 backend
// (BallisticEngine.DX12/Resources/Dx12Ddgi.cs) — but the Engine/Editor layers may NOT reference DX12
// (layering rule), so this re-derives the SAME camera-centered snap placement on the CPU.
//
// ★ SYNC CONTRACT: these constants + Snap()/ProbePosition() MUST stay byte-for-byte equal to
//   Dx12Ddgi.ProbesX/Y/Z, Dx12Ddgi.Spacing (default), and Dx12Ddgi.Update()/ProbePosition(). Both
//   sides are trivial (a round-to-spacing snap) and are documented as a pair. If the DDGI grid is ever
//   made dynamic (spacing/dims from the volume), publish the real grid through a read-only render-state
//   struct instead of mirroring here — see GI Pragmatic Revival plan R2.4 / the GiDebugViewer follow-up.
//
// The gizmo is placement-only: it shows WHERE the probes/cascade are, not their irradiance (that needs a
// GPU atlas readback — deferred stage 2). The covered volume = Spacing * (Probes-1) per axis.
public static class GiDebugGrid {
    // Mirror of Dx12Ddgi.ProbesX/Y/Z (16 x 8 x 16 = 2048 probes).
    public const int ProbesX = 16, ProbesY = 8, ProbesZ = 16;
    public const int ProbeCount = ProbesX * ProbesY * ProbesZ;

    // Mirror of Dx12Ddgi.Spacing default (2 m → ~30 x 14 x 30 m covered volume).
    public static readonly Vector3 DefaultSpacing = new(2f, 2f, 2f);

    // Camera-centered snap — identical to Dx12Ddgi.Update(): place the corner probe so the camera sits
    // near the grid centre, snapped to whole probe spacings (probes don't swim under sub-cell motion).
    public static Vector3 Snap(Vector3 cameraPos, Vector3 spacing) {
        Vector3 half = new(
            spacing.X * (ProbesX - 1) * 0.5f,
            spacing.Y * (ProbesY - 1) * 0.5f,
            spacing.Z * (ProbesZ - 1) * 0.5f);
        Vector3 snapped = new(
            MathF.Round(cameraPos.X / spacing.X) * spacing.X,
            MathF.Round(cameraPos.Y / spacing.Y) * spacing.Y,
            MathF.Round(cameraPos.Z / spacing.Z) * spacing.Z);
        return snapped - half;   // Origin = corner probe world position
    }

    // World position of probe (px,py,pz) given a snapped origin — mirror of Dx12Ddgi.ProbePosition().
    public static Vector3 ProbePosition(Vector3 origin, Vector3 spacing, int px, int py, int pz) =>
        origin + new Vector3(px * spacing.X, py * spacing.Y, pz * spacing.Z);

    // The covered-volume AABB size (the cascaded GI bound the gizmo draws as a wirecube).
    public static Vector3 CoveredSize(Vector3 spacing) => new(
        spacing.X * (ProbesX - 1),
        spacing.Y * (ProbesY - 1),
        spacing.Z * (ProbesZ - 1));

    // ── Probe irradiance COLOUR bridge (DX12 → Editor gizmo). The DX12 backend periodically reads the DDGI
    // irradiance atlas back to the CPU, averages each probe's tile to one mean colour, and publishes it here;
    // the Volume gizmo (ShowProbeSpheres) reads it to tint each sphere with the real bounce colour it caches —
    // the "I can't see the colour data in the spheres" view. This is plain CPU data (no DX12 types), so the
    // Engine/Editor layers can read it without breaking the layering rule. RAW HDR linear (the gizmo tonemaps).
    // ProbeColorFrame increments on every publish so the gizmo knows the data is live (and how stale).
    public static readonly Vector3[] ProbeColors = new Vector3[ProbeCount];
    public static bool HasProbeColors;     // set true after the first publish (else the gizmo uses a placeholder)
    public static int ProbeColorFrame;     // bumped each publish; lets the gizmo show "no live data" if frozen

    // The gizmo SETS this each frame it wants live probe colours (ShowProbeSpheres on). The DX12 DDGI pass reads
    // it and does a THROTTLED atlas readback only while it's requested — so the GPU readback cost is paid only
    // when the debug view is actually open, never in normal rendering. Stored as a frame stamp: the gizmo writes
    // the current frame; the renderer compares against its own counter to decide "requested recently".
    public static bool ProbeColorsRequested;
    public static void RequestProbeColors() => ProbeColorsRequested = true;

    // CHUNK5 manual "Rebake GI" button: the editor/remote sets this; the DX12 DDGI pass consumes it once per
    // frame and calls Rebake() on both cascades (re-runs the progressive bake near-first). A one-shot flag — the
    // renderer clears it after acting. Cross-thread plain bool is fine (set rarely, read once/frame).
    public static bool RebakeRequested;
    public static void RequestRebake() => RebakeRequested = true;

    // Called by the DX12 DDGI readback. `colors` is indexed by the SAME probe flattening as ProbePosition's
    // (pz*ProbesY + py)*ProbesX + px order Dx12Ddgi uses. Copies in (the gizmo reads on the main thread).
    public static void PublishProbeColors(System.ReadOnlySpan<Vector3> colors) {
        int n = System.Math.Min(colors.Length, ProbeColors.Length);
        for (int i = 0; i < n; i++) ProbeColors[i] = colors[i];
        HasProbeColors = true;
        ProbeColorFrame++;
    }

    // Probe flat index in the DDGI/atlas order — the gizmo maps its (px,py,pz) loop to a ProbeColors index.
    public static int ProbeIndex(int px, int py, int pz) => (pz * ProbesY + py) * ProbesX + px;
}
