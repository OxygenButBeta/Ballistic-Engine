
namespace BallisticEngine;

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
