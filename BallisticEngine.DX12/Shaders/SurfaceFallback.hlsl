// Fallback Surface body — the black/magenta checker shown when a custom shader fails to compile (at
// load OR on hot-reload). It is a Surface() snippet appended after SurfaceSkeleton.hlsl with
// CUSTOM_SURFACE defined (so the skeleton's default Standard body is omitted). World-position-based
// so it's visible even on UV-less meshes, and emissive so it shows up unlit — the error must be
// OBVIOUS in the viewport, never a silent black surface or a crash.

SurfaceOutput Surface(SurfaceInput i) {
    SurfaceOutput s;
    // 0.5m world-space checker parity → magenta / black.
    float3 cell = floor(i.PosW * 2.0);
    float checker = frac((cell.x + cell.y + cell.z) * 0.5) > 0.25 ? 1.0 : 0.0;
    float3 magenta = float3(1.0, 0.0, 1.0);
    s.Albedo   = lerp(float3(0.02, 0.02, 0.02), magenta, checker);
    s.Emissive = s.Albedo;            // emit so it's visible with no lighting
    s.Normal   = normalize(i.NormalW);
    s.Metallic = 0.0;
    s.Roughness = 1.0;
    s.AO = 1.0;
    s.Alpha = 1.0;
    return s;
}
