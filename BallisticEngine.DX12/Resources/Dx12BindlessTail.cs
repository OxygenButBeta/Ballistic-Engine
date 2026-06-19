namespace BallisticEngine.DX12;

// Reserved descriptor slots at the top of the shader-visible bindless heap.
internal static class Dx12BindlessTail
{
    public const int HeapCapacity = 16384;

    const int RtReflReserved = 32;
    public const int RtReflUsed = 8;

    public const int RtReflTableBase = HeapCapacity - RtReflReserved;

    // Lumen V2 GI trace table — its OWN reserved tail BELOW the RT-reflection tail so the two never collide.
    // Slots used (7): t1 depth, t2 normal, t3 material, t4 lit scene color, t5 sky irradiance cube, t6 sky
    // prefilter cube, u0 indirect UAV. (TLAS t0 + CardRadiance/InstanceMeta/materials/lights are ROOT SRVs,
    // not table slots.)
    const int LumenReserved = 16;
    public const int LumenUsed = 9;   // #3: +7 = probe history SRV (t14), +8 = motion SRV (t15, ghosting reject)
    public const int LumenTableBase = RtReflTableBase - LumenReserved;

    // Lumen V2 card-LIGHTING pass table — one slot (the sky irradiance cube). Below the GI tail.
    const int LumenCardReserved = 8;
    public const int LumenCardUsed = 1;
    public const int LumenCardTableBase = LumenTableBase - LumenCardReserved;

    // Lumen V2 Sıra 1 — SCREEN-PROBE trace table (its own tail below the card tail). The probe trace mirrors the
    // GI trace's binding shape: t1 depth, t2 normal, t3 material, t4 lit scene color, t5 sky irradiance, t6 sky
    // prefilter (6 SRV) + u1 probe atlas UAV (u0 ProbeHeaders + u2 Indirect are ROOT UAVs). 9 reserved for slack.
    const int LumenScreenProbeReserved = 12;
    public const int LumenScreenProbeUsed = 7;   // t1-t6 SRV + u1 atlas UAV
    public const int LumenScreenProbeTableBase = LumenCardTableBase - LumenScreenProbeReserved;

    public const int TailStart = LumenScreenProbeTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_LumenFits = 1 / (LumenUsed <= LumenReserved ? 1 : 0);
    const int A_LumenCardFits = 1 / (LumenCardUsed <= LumenCardReserved ? 1 : 0);
    const int A_LumenScreenProbeFits = 1 / (LumenScreenProbeUsed <= LumenScreenProbeReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_LumenFits + A_LumenCardFits + A_LumenScreenProbeFits + A_TailStartPositive;
}
