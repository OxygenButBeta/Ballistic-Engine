using BallisticEngine.Networking;

namespace BallisticEngine;

public static class NetworkReplicationRegistry {
    static readonly Dictionary<int, NetworkTypeDescriptor> byTypeId = new();

    public static int Count => byTypeId.Count;

    public static void Register(NetworkTypeDescriptor descriptor) {
        byTypeId[descriptor.TypeId] = descriptor;
    }

    public static bool TryGet(int typeId, out NetworkTypeDescriptor descriptor) =>
        byTypeId.TryGetValue(typeId, out descriptor);

    public static NetworkTypeDescriptor Get(int typeId) =>
        byTypeId.TryGetValue(typeId, out var d) ? d : default;

    public static IReadOnlyCollection<NetworkTypeDescriptor> All => byTypeId.Values;

    public static bool TryGetRpc(int typeId, int methodId, out NetworkRpcEntry entry) {
        if (byTypeId.TryGetValue(typeId, out NetworkTypeDescriptor d))
            return d.TryGetRpc(methodId, out entry);
        entry = default;
        return false;
    }

    public static void ClearForReload() => byTypeId.Clear();
}

public delegate void NetworkRpcInvoker(NetworkBehaviour self, ref BitReader args, Connection caller);

public readonly struct NetworkRpcEntry {
    public readonly int MethodId;
    public readonly RpcTarget Target;
    public readonly bool Reliable;
    public readonly NetworkRpcInvoker Invoke;

    public NetworkRpcEntry(int methodId, RpcTarget target, bool reliable, NetworkRpcInvoker invoke) {
        MethodId = methodId;
        Target = target;
        Reliable = reliable;
        Invoke = invoke;
    }
}

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
