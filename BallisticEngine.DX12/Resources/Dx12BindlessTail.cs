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

    public const int TailStart = GlobalSdfTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_AuroraFits = 1 / (AuroraUsed <= AuroraReserved ? 1 : 0);
    const int A_AuroraCardFits = 1 / (AuroraCardUsed <= AuroraCardReserved ? 1 : 0);
    const int A_AuroraScreenProbeFits = 1 / (AuroraScreenProbeUsed <= AuroraScreenProbeReserved ? 1 : 0);
    const int A_GlobalSdfFits = 1 / (GlobalSdfUsed <= GlobalSdfReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_AuroraFits + A_AuroraCardFits + A_AuroraScreenProbeFits + A_GlobalSdfFits + A_TailStartPositive;
}
