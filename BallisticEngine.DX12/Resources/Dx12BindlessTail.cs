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

    public const int TailStart = LumenCardTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_LumenFits = 1 / (LumenUsed <= LumenReserved ? 1 : 0);
    const int A_LumenCardFits = 1 / (LumenCardUsed <= LumenCardReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_LumenFits + A_LumenCardFits + A_TailStartPositive;
}
