
namespace BallisticEngine;

// One source-model node, so the editor can rebuild the model's authored hierarchy as entities.
// Nodes are stored in pre-order: ParentIndex always refers to an earlier entry (-1 for the
// root), and LocalTransform is relative to that parent — NOT model space (SubMeshData's
// NodeTransform carries the model-space matrix the renderer needs). Written by split-by-nodes
// imports; merged imports and legacy artifacts have an empty node table.
public readonly struct MeshNodeData {
    public readonly string Name;
    public readonly int ParentIndex;
    public readonly Matrix4 LocalTransform;

    public MeshNodeData(string name, int parentIndex, Matrix4 localTransform) {
        Name = name;
        ParentIndex = parentIndex;
        LocalTransform = localTransform;
    }
}
