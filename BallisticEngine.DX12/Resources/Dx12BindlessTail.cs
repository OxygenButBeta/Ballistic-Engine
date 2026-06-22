namespace BallisticEngine.DX12;

internal static class Dx12BindlessTail
{
    public const int HeapCapacity = 16384;

    const int RtReflReserved = 32;
    public const int RtReflUsed = 8;

    public const int RtReflTableBase = HeapCapacity - RtReflReserved;

    // FAZ 1: GI tail sökülü — TailStart = RtReflTableBase (no-GI durumu).
    // FAZ 2'de Aurora'nın 3-tail layout'u (GI / Card / ScreenProbe) buraya gelecek.
    public const int TailStart = RtReflTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_TailStartPositive;
}
