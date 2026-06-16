namespace BallisticEngine;

// A generational handle to a networked object (plan §8.4). The ONLY safe way to hold a cross-object
// reference to a NetworkBehaviour: a plain `Pawn pawn` field dangles after despawn (GC keeps it alive),
// so an "ordinary null check" lies. NetworkRef binds to (slot, generation) via the netId — when the
// object despawns the registry bumps the slot's generation, so this handle resolves to null instead of
// a stale object. This is what makes §3's "no public netId, refs null on despawn" guarantee real.
//
// Ergonomics (the §14 item 2 decision): implicit capture from T (so `NetworkRef<Pawn> r = pawn;` just
// works), explicit `.Value` to read back (Fusion's NetworkBehaviourRef style — the deref is a registry
// lookup, so it's NOT hidden behind an implicit T conversion that could fire inside a hot loop). Compare
// handles with == (value semantics on the netId). Default(NetworkRef<T>) is a null handle.
//
// Resolve cost (§14 item 2): .Value is one slot-array index + an int generation compare
// (NetworkObjectRegistry) — no dictionary hash, safe to deref in NetworkTick. For a tight loop that
// derefs the same ref many times, cache `.Value` once at the top of the tick (validate generation once,
// reuse the object) rather than re-resolving per use.
public readonly struct NetworkRef<T> : IEquatable<NetworkRef<T>> where T : NetworkBehaviour {
    // The packed netId of the referenced object's NetworkObject (slot | generation<<bits). 0 = null
    // handle. We store the netId, not the object, so the generation check happens on every resolve.
    readonly int netId;

    internal NetworkRef(int netId) => this.netId = netId;

    // Capture a live object into a handle. Null / unspawned -> a null handle.
    public NetworkRef(T target) =>
        netId = target?.NetworkObject is { IsSpawned: true } no ? no.NetId : 0;

    // Resolve to the live object, or null if despawned (generation mismatch) / never set. The hot path.
    public T Value {
        get {
            if (netId == 0)
                return null;
            NetworkObject no = Network.Resolve(netId);          // slot index + generation compare
            if (no is null)
                return null;
            // The handle addresses the NetworkObject; the referenced component is a T on its entity.
            return no.Entity?.GetComponent<T>();
        }
    }

    // True when the referenced object is still spawned (and the generation still matches).
    public bool IsAlive => Network.Resolve(netId) is not null;

    // A null handle (never set, or captured from null/unspawned).
    public bool IsNull => netId == 0;

    // Implicit capture: `NetworkRef<Pawn> r = pawn;` — convenient at the API edge. NOT the reverse
    // (handle -> T is `.Value`, explicit, so a deref never hides inside a hot loop).
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
