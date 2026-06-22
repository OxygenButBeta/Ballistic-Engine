
namespace BallisticEngine;

public readonly struct LodRange {
    public readonly int FirstIndex;
    public readonly int IndexCount;
    public LodRange(int firstIndex, int indexCount) { FirstIndex = firstIndex; IndexCount = indexCount; }
}

public readonly struct SubMeshData {
    public readonly string Name;
    public readonly int IndexStart;
    public readonly int IndexCount;
    public readonly string MaterialRef;

    public readonly Matrix4 NodeTransform;

    public readonly int NodeIndex;

    public readonly LodRange[] Lods;

    public SubMeshData(string name, int indexStart, int indexCount, string materialRef)
        : this(name, indexStart, indexCount, materialRef, Matrix4.Identity, -1) {
    }

    public SubMeshData(string name, int indexStart, int indexCount, string materialRef,
        Matrix4 nodeTransform, int nodeIndex = -1, LodRange[] lods = null) {
        Name = name;
        IndexStart = indexStart;
        IndexCount = indexCount;
        MaterialRef = materialRef;
        NodeTransform = nodeTransform;
        NodeIndex = nodeIndex;
        Lods = lods;
    }

    public LodRange LodAt(int lod) {
        if (Lods is not { Length: > 1 } || lod <= 0) return new LodRange(IndexStart, IndexCount);
        return Lods[Math.Min(lod, Lods.Length - 1)];
    }
    public int LodCount => Lods is { Length: > 1 } ? Lods.Length : 1;

    public SubMeshData WithMaterialRef(string materialRef) =>
        new(Name, IndexStart, IndexCount, materialRef, NodeTransform, NodeIndex, Lods);

    public SubMeshData WithLods(LodRange[] lods) =>
        new(Name, IndexStart, IndexCount, materialRef: MaterialRef, NodeTransform, NodeIndex, lods);
}
