using BallisticEngine.Networking;

namespace BallisticEngine;

public readonly struct NetworkTypeDescriptor {
    public readonly int TypeId;
    public readonly int LayoutHash;
    public readonly string TypeName;
    public readonly NetworkRpcEntry[] Rpcs;
    public readonly Type ComponentType;

    public NetworkTypeDescriptor(int typeId, int layoutHash, string typeName, NetworkRpcEntry[] rpcs,
        Type componentType = null) {
        TypeId = typeId;
        LayoutHash = layoutHash;
        TypeName = typeName;
        Rpcs = rpcs ?? Array.Empty<NetworkRpcEntry>();
        ComponentType = componentType;
    }

    public bool TryGetRpc(int methodId, out NetworkRpcEntry entry) {
        for (int i = 0; i < Rpcs.Length; i++) {
            if (Rpcs[i].MethodId == methodId) { entry = Rpcs[i]; return true; }
        }
        entry = default;
        return false;
    }
}
