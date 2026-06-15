
namespace BallisticEngine;

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

    public SubMeshData(string name, int indexStart, int indexCount, string materialRef)
        : this(name, indexStart, indexCount, materialRef, Matrix4.Identity, -1) {
    }

    public SubMeshData(string name, int indexStart, int indexCount, string materialRef,
        Matrix4 nodeTransform, int nodeIndex = -1) {
        Name = name;
        IndexStart = indexStart;
        IndexCount = indexCount;
        MaterialRef = materialRef;
        NodeTransform = nodeTransform;
        NodeIndex = nodeIndex;
    }

    public SubMeshData WithMaterialRef(string materialRef) =>
        new(Name, IndexStart, IndexCount, materialRef, NodeTransform, NodeIndex);
}
