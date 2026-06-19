namespace BallisticEngine.DX12;

// Reserved descriptor slots at the top of the shader-visible bindless heap.
internal static class Dx12BindlessTail
{
    public const int HeapCapacity = 16384;

    const int RtReflReserved = 32;
    public const int RtReflUsed = 8;

    public const int RtReflTableBase = HeapCapacity - RtReflReserved;

    // Lumen V2 GI trace table — its OWN reserved tail BELOW the RT-reflection tail so the two never collide.
    // Slots used (7): t0 TLAS, t1 depth, t2 normal, t3 material, t4 lit scene color, t5 sky irradiance cube,
    // t6 sky prefilter cube; u0 (indirect UAV) lives in a committed descriptor outside the bindless heap.
    const int LumenReserved = 16;
    public const int LumenUsed = 7;
    public const int LumenTableBase = RtReflTableBase - LumenReserved;

    public const int TailStart = LumenTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_LumenFits = 1 / (LumenUsed <= LumenReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_LumenFits + A_TailStartPositive;
}
