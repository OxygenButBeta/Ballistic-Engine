
namespace BallisticEngine;

public readonly struct LodRange {
    public readonly int FirstIndex;
    public readonly int IndexCount;
    public LodRange(int firstIndex, int indexCount) { FirstIndex = firstIndex; IndexCount = indexCount; }
}
