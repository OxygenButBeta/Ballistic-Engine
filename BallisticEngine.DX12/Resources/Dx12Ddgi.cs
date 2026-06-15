using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DDGI world-probe radiance cache (GI plan P2 — the chosen P2, replacing the Lumen mesh-card surface cache;
// see Docs/Plans/dx12-lumen-gi-plan.md Phase 2). A camera-centered 3D grid of irradiance probes: each probe
// stores incoming radiance as a small OCTAHEDRAL irradiance map + a depth-moments map (mean + mean-squared
// distance) for the Chebyshev visibility (leak) test. Probes are updated by tracing rays against the scene
// TLAS and shading the hit with the EXISTING P1 world-radiance path (DxrGi.hlsl), blended over time with a
// hysteresis EMA — so multi-bounce is free (update rays read last frame's probe field) and the result is
// stable under camera motion. A shading point gathers the 8 enclosing probes, trilinear + Chebyshev-weighted.
// Published technique (Majercik et al. 2019 JCGT; NVIDIA RTXGI) — no bake, no authoring, fully dynamic.
//
// P2.0 (this file's first cut): the GRID + the two atlas textures (irradiance + depth) as UAV/SRV, the
// constants, and camera-centered placement. The update/blend/gather compute passes land in P2.1+.
public sealed class Dx12Ddgi : IDisposable {
    readonly Dx12Device dev;

    // --- Grid dimensions (probes per axis). Start modest; tune to the GTX-1660 VRAM/ray budget in P2.5.
    // 16 x 8 x 16 = 2048 probes. Camera-centered, snapped to the probe spacing so it slides smoothly.
    public const int ProbesX = 16, ProbesY = 8, ProbesZ = 16;
    public const int ProbeCount = ProbesX * ProbesY * ProbesZ;

    // --- Octahedral tile sizes (interior texels; a 1px border is added for correct bilinear wrap at edges).
    public const int IrradianceTexels = 6;    // 6x6 octahedral irradiance per probe (RGBA16F)
    public const int DepthTexels = 16;         // 16x16 octahedral depth moments per probe (RG16F)
    const int Border = 1;
    const int IrrTile = IrradianceTexels + 2 * Border;   // 8
    const int DepthTile = DepthTexels + 2 * Border;       // 18

    // Atlas layout: probes flattened as a 2D grid of tiles, (ProbesX*ProbesZ) columns x ProbesY rows. So a
    // probe (px,py,pz) → tile column = pz*ProbesX + px, tile row = py. One draw/dispatch covers the atlas.
    public const int TilesWide = ProbesX * ProbesZ;       // 256
    public const int TilesHigh = ProbesY;                  // 8
    public static int IrradianceAtlasW => TilesWide * IrrTile;      // 2048
    public static int IrradianceAtlasH => TilesHigh * IrrTile;      // 64
    public static int DepthAtlasW => TilesWide * DepthTile;         // 4608
    public static int DepthAtlasH => TilesHigh * DepthTile;         // 144

    // Atlas textures (compute-written UAV + gather-read SRV). The atlases are PERSISTENT resources; their
    // descriptors are created per-dispatch into a shader-visible heap by the update/gather passes (P2.1+) —
    // NOT registered in Dx12Backend.BindlessHeap, which the material table Resets (would clobber them).
    public ID3D12Resource IrradianceTex => irradianceTex;
    public ID3D12Resource DepthTex => depthTex;
    ID3D12Resource irradianceTex, depthTex;

    // --- Grid placement (world space). Origin = the corner probe; spacing = metres between probes. The grid
    // is camera-centered: re-snapped each frame to the camera so coverage follows the view (a single clipmap
    // cascade for now). ProbeSpacing sets the covered volume = spacing * (probes-1) per axis.
    public Vector3 Origin { get; private set; }
    public Vector3 Spacing { get; private set; } = new(2.0f, 2.0f, 2.0f);   // 2m → ~30x14x30m covered volume

    public bool Allocated => irradianceTex != null;

    // Per-pass constants shared by update/blend/gather (std140-ish; matches Ddgi.hlsl). Kept here so every
    // pass sees ONE grid definition.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct DdgiConstants {
        public Vector4 OriginSpacingX;   // xyz = grid origin (world), w = spacing.x
        public Vector4 SpacingYZ;        // x = spacing.y, y = spacing.z, z/w = pad
        public Vector4 ProbeDims;        // xyz = (ProbesX,ProbesY,ProbesZ) as floats, w = ProbeCount
        public Vector4 Params0;          // x=irrTexels y=depthTexels z=hysteresis w=frameIndex
        public Vector4 Params1;          // x=maxRayDist y=normalBias z=viewBias w=intensity
    }

    public Dx12Ddgi(Dx12Device device) { dev = device; }

    public void EnsureAllocated() {
        if (Allocated) return;
        irradianceTex = CreateAtlas(IrradianceAtlasW, IrradianceAtlasH, Format.R16G16B16A16_Float);
        depthTex = CreateAtlas(DepthAtlasW, DepthAtlasH, Format.R16G16_Float);
    }

    // Camera-centered snap: place the grid so the camera sits near its centre, snapped to whole probe
    // spacings (so probes don't swim under sub-cell camera motion → temporal stability). Call per frame.
    public void Update(Vector3 cameraPos) {
        Vector3 half = new(
            Spacing.X * (ProbesX - 1) * 0.5f,
            Spacing.Y * (ProbesY - 1) * 0.5f,
            Spacing.Z * (ProbesZ - 1) * 0.5f);
        Vector3 snapped = new(
            MathF.Round(cameraPos.X / Spacing.X) * Spacing.X,
            MathF.Round(cameraPos.Y / Spacing.Y) * Spacing.Y,
            MathF.Round(cameraPos.Z / Spacing.Z) * Spacing.Z);
        Origin = snapped - half;
    }

    public DdgiConstants Constants(int frameIndex, float hysteresis, float intensity) => new() {
        OriginSpacingX = new Vector4(Origin, Spacing.X),
        SpacingYZ = new Vector4(Spacing.Y, Spacing.Z, 0, 0),
        ProbeDims = new Vector4(ProbesX, ProbesY, ProbesZ, ProbeCount),
        Params0 = new Vector4(IrradianceTexels, DepthTexels, hysteresis, frameIndex),
        Params1 = new Vector4(40f, 0.25f, 0.1f, intensity),
    };

    // World position of probe (px,py,pz) — for the debug gizmo + the update pass.
    public Vector3 ProbePosition(int px, int py, int pz) =>
        Origin + new Vector3(px * Spacing.X, py * Spacing.Y, pz * Spacing.Z);

    ID3D12Resource CreateAtlas(int w, int h, Format fmt) {
        var desc = ResourceDescription.Texture2D(fmt, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        return dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
    }

    public void Dispose() {
        irradianceTex?.Dispose(); irradianceTex = null;
        depthTex?.Dispose(); depthTex = null;
    }
}
