namespace BallisticEngine.DX12;

// Reserved descriptor slots at the top of the shader-visible bindless heap.
internal static class Dx12BindlessTail
{
    public const int HeapCapacity = 16384;

    const int RtReflReserved = 32;
    public const int RtReflUsed = 8;

    public const int RtReflTableBase = HeapCapacity - RtReflReserved;

    // DDGI relight table — its OWN reserved tail BELOW the RT-reflection tail so the two never collide.
    // Slots used: t1 sky irradiance cube (the per-probe RT trace samples it on a ray miss). TLAS + bindless
    // geo/material/lights are ROOT SRVs (not table slots). 8 reserved for slack.
    const int DdgiRelightReserved = 8;
    public const int DdgiRelightUsed = 1;
    public const int DdgiRelightTableBase = RtReflTableBase - DdgiRelightReserved;

    public const int TailStart = DdgiRelightTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_DdgiRelightFits = 1 / (DdgiRelightUsed <= DdgiRelightReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_DdgiRelightFits + A_TailStartPositive;
}
