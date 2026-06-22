using BallisticEngine.Networking;

namespace BallisticEngine;

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
