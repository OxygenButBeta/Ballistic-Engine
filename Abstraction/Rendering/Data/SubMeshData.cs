
namespace BallisticEngine;

// One LOD's index-buffer range for a submesh. All LODs of a mesh live in the SAME shared index buffer (LOD0
// ranges first, then LOD1.., appended), referencing the SAME shared vertex buffer (decimation is index-only,
// BaseVertexLocation stays 0) — so the GPU-driven renderer selects a LOD by FirstIndex/IndexCount alone.
public readonly struct LodRange {
    public readonly int FirstIndex;
    public readonly int IndexCount;
    public LodRange(int firstIndex, int indexCount) { FirstIndex = firstIndex; IndexCount = indexCount; }
}

// A contiguous index-buffer range of a mesh that renders with one material.
// MaterialRef is an "Assets/..." reference baked by the model importer (the .mat it generated
// for the source material), or null when the source had no material — the renderer then falls
// back to its explicitly assigned SharedMaterial.
public readonly struct SubMeshData {
    public readonly string Name;        // source node name (split imports) or material name
    public readonly int IndexStart;     // offset into the mesh's index buffer
    public readonly int IndexCount;
    public readonly string MaterialRef;

    // The source node's local-to-model matrix. Vertices are baked in model space, so rendering
    // never needs this; it carries the node's pivot for per-node entity instantiation (a child
    // entity takes this as its transform and the renderer un-bakes it with the inverse).
    // Identity for merged-by-material imports and pre-v4 artifacts.
    public readonly Matrix4 NodeTransform;

    // Index into MeshData.Nodes (the source node this submesh belongs to), or -1 when the
    // import didn't record a node table (merged imports, pre-v5 artifacts).
    public readonly int NodeIndex;

    // Geometric LOD ranges for this submesh (LOD0..N), all in the mesh's shared index buffer. Null or length<=1
    // ⇒ LOD0-only (the renderer always draws (IndexStart,IndexCount) → byte-identical to a pre-LOD artifact).
    // INVARIANT (the render side relies on it): Lods[0] == (IndexStart, IndexCount).
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

    // LOD range for `lod` (clamped). Returns (IndexStart,IndexCount) for LOD0 or when no chain is present →
    // unchanged behaviour for pre-LOD meshes.
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
