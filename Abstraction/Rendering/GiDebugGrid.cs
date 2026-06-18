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
}
