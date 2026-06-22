namespace BallisticEngine;

public static class SceneReplicationRegistry {
    static readonly Dictionary<int, SceneReplDescriptor> byTypeId = new();

    public static int Count => byTypeId.Count;

    public static void Register(SceneReplDescriptor descriptor) {
        byTypeId[descriptor.TypeId] = descriptor;
    }

    public static bool TryGet(int typeId, out SceneReplDescriptor descriptor) =>
        byTypeId.TryGetValue(typeId, out descriptor);

    public static IReadOnlyCollection<SceneReplDescriptor> All => byTypeId.Values;

    public static void ClearForReload() => byTypeId.Clear();
}

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
