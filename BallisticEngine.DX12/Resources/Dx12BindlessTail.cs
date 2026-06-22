namespace BallisticEngine.DX12;

internal static class Dx12BindlessTail
{
    public const int HeapCapacity = 16384;

    const int RtReflReserved = 32;
    public const int RtReflUsed = 8;

    public const int RtReflTableBase = HeapCapacity - RtReflReserved;

    const int DdgiRelightReserved = 8;
    public const int DdgiRelightUsed = 1;
    public const int DdgiRelightTableBase = RtReflTableBase - DdgiRelightReserved;

    public const int TailStart = DdgiRelightTableBase;

    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_DdgiRelightFits = 1 / (DdgiRelightUsed <= DdgiRelightReserved ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    static Dx12BindlessTail() => _ = A_RtReflFits + A_DdgiRelightFits + A_TailStartPositive;
}
