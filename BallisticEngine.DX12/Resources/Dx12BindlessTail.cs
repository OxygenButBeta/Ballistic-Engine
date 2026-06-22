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

    public const int TailStart = AuroraScreenProbeTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_AuroraFits = 1 / (AuroraUsed <= AuroraReserved ? 1 : 0);
    const int A_AuroraCardFits = 1 / (AuroraCardUsed <= AuroraCardReserved ? 1 : 0);
    const int A_AuroraScreenProbeFits = 1 / (AuroraScreenProbeUsed <= AuroraScreenProbeReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_AuroraFits + A_AuroraCardFits + A_AuroraScreenProbeFits + A_TailStartPositive;
}
