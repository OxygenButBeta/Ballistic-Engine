namespace BallisticEngine.DX12;

internal static class Dx12BindlessTail
{
    public const int HeapCapacity = 16384;

    const int RtReflReserved = 32;
    public const int RtReflUsed = 8;

    public const int RtReflTableBase = HeapCapacity - RtReflReserved;

    // Aurora GI trace table — its OWN reserved tail BELOW the RT-reflection tail so the two never collide.
    const int AuroraReserved = 16;
    public const int AuroraUsed = 9;   // +7 = probe history SRV (t14), +8 = motion SRV (t15, ghosting reject)
    public const int AuroraTableBase = RtReflTableBase - AuroraReserved;

    // Aurora card-LIGHTING pass table — one slot (the sky irradiance cube). Below the GI tail.
    const int AuroraCardReserved = 8;
    public const int AuroraCardUsed = 1;
    public const int AuroraCardTableBase = AuroraTableBase - AuroraCardReserved;

    // Aurora SCREEN-PROBE trace table (its own tail below the card tail).
    const int AuroraScreenProbeReserved = 16;
    public const int AuroraScreenProbeUsed = 9;
    public const int AuroraScreenProbeTableBase = AuroraCardTableBase - AuroraScreenProbeReserved;

    // Lumen FAZ 2 GLOBAL DISTANCE FIELD — its own reserved tail below the Aurora screen-probe tail. These slots are
    // PERSISTENT (the clipmap UAV + each unique mesh's SDF SRV are stamped once and never re-allocated), so they MUST
    // live OUTSIDE the dynamic Allocate()/Reset() cursor region that Dx12GpuDrivenRenderer.EnsureMaterialTable rewinds
    // — otherwise the GPU-driven material/geometry re-stamp clobbers them and the composite reads garbage descriptors
    // (typed-mismatch UAV/SRV → GPU page fault → device removed). Slot layout: +0 = clipmap UAV (u0 table),
    // +1..+GlobalSdfMaxTextures = per-mesh SDF SRVs (ResourceDescriptorHeap[]). Door-gated; nothing allocated when off.
    public const int GlobalSdfMaxTextures = 254;                          // unique mesh SDFs (CornellBox/GI scenes: a handful)
    const int GlobalSdfReserved = GlobalSdfMaxTextures + 2;               // +1 clipmap UAV, +1 slack
    public const int GlobalSdfUsed = GlobalSdfMaxTextures + 1;
    public const int GlobalSdfTableBase = AuroraScreenProbeTableBase - GlobalSdfReserved;

    // Lumen FAZ 3b/3d SURFACE-CACHE ATLAS — its own reserved tail below the global-SDF tail. Holds the PERSISTENT
    // SRV+UAV pair for each physical-atlas texture. FAZ 3b: Albedo/Normal/Emissive/Depth/DirectLighting/FinalLighting
    // = 6 atlases × 2 = 12 slots. FAZ 3d adds a SECOND FinalLighting atlas (finalLightB) for the multi-bounce
    // ping-pong (read last frame's lit cache while writing this frame's) = +2 → 14 slots. Same rule as every block
    // above: these are stamped ONCE (the atlas resources never re-allocate), so they MUST live OUTSIDE the dynamic
    // Allocate()/Reset() cursor the GPU-driven material table rewinds — else the re-stamp clobbers them (typed-mismatch
    // descriptor → GPU page fault → device removed). Door-gated; nothing allocated when Lumen cards are off. Slot
    // order: per atlas, SRV then UAV, in atlas-creation order.
    const int LumenSurfaceCacheReserved = 16;   // 14 used (7 atlases × SRV+UAV) + slack
    public const int LumenSurfaceCacheUsed = 14;
    public const int LumenSurfaceCacheTableBase = GlobalSdfTableBase - LumenSurfaceCacheReserved;

    public const int TailStart = LumenSurfaceCacheTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_AuroraFits = 1 / (AuroraUsed <= AuroraReserved ? 1 : 0);
    const int A_AuroraCardFits = 1 / (AuroraCardUsed <= AuroraCardReserved ? 1 : 0);
    const int A_AuroraScreenProbeFits = 1 / (AuroraScreenProbeUsed <= AuroraScreenProbeReserved ? 1 : 0);
    const int A_GlobalSdfFits = 1 / (GlobalSdfUsed <= GlobalSdfReserved ? 1 : 0);
    const int A_LumenSurfaceCacheFits = 1 / (LumenSurfaceCacheUsed <= LumenSurfaceCacheReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_AuroraFits + A_AuroraCardFits + A_AuroraScreenProbeFits + A_GlobalSdfFits + A_LumenSurfaceCacheFits + A_TailStartPositive;
}
