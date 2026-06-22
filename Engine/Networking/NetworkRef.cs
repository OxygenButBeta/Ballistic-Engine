namespace BallisticEngine;

public readonly struct NetworkRef<T> : IEquatable<NetworkRef<T>> where T : NetworkBehaviour {
    readonly int netId;

    internal NetworkRef(int netId) => this.netId = netId;

    public NetworkRef(T target) =>
        netId = target?.NetworkObject is { IsSpawned: true } no ? no.NetId : 0;

    public T Value {
        get {
            if (netId == 0)
                return null;
            NetworkObject no = Network.Resolve(netId);
            if (no is null)
                return null;
            return no.Entity?.GetComponent<T>();
        }
    }

    public bool IsAlive => Network.Resolve(netId) is not null;

    public bool IsNull => netId == 0;

    public static implicit operator NetworkRef<T>(T target) => new(target);

    public bool Equals(NetworkRef<T> other) => netId == other.netId;
    public override bool Equals(object obj) => obj is NetworkRef<T> r && Equals(r);
    public override int GetHashCode() => netId;
    public static bool operator ==(NetworkRef<T> a, NetworkRef<T> b) => a.netId == b.netId;
    public static bool operator !=(NetworkRef<T> a, NetworkRef<T> b) => a.netId != b.netId;

    public override string ToString() => netId == 0 ? $"NetworkRef<{typeof(T).Name}>(null)"
        : IsAlive ? $"NetworkRef<{typeof(T).Name}>(#{netId})"
        : $"NetworkRef<{typeof(T).Name}>(#{netId}, despawned)";
}
