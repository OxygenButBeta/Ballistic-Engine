namespace BallisticEngine;

public readonly struct SceneReplDescriptor {
    public readonly int TypeId;
    public readonly int LayoutHash;
    public readonly string TypeName;

    public SceneReplDescriptor(int typeId, int layoutHash, string typeName) {
        TypeId = typeId;
        LayoutHash = layoutHash;
        TypeName = typeName;
    }
}
