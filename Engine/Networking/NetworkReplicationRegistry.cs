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
