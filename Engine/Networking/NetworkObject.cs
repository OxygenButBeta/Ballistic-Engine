using BallisticEngine.Networking;

namespace BallisticEngine;

// Net identity + authority/ownership holder (plan §2) — the UNIT that spawns/despawns; the
// [Networked] state of every NetworkBehaviour on the same entity lives under it. A Behaviour, so the
// editor/serializer/registry discover it free (§10).
//
// P0 scope (the B3 skeleton, §14 0b): identity (netId, owner), the role flags, IsSpawned, and the
// spawn/despawn marks. Full authority arrives in P1 (the §4d.1 truth-table); in loopback the local
// host has Both authority over its own objects, so the skeleton resolves trivially-correctly today.
//
// netId is INTERNAL — never a public field (§3). Game code addresses objects by typed reference; the
// generational NetworkRef<T> handle (§8.4) that nulls on despawn is a P1 deliverable.
[Component("Network Object", "Networking")]
public sealed class NetworkObject : Behaviour {
    // Registry slot assigned at spawn (Network.Spawn). 0 = unspawned. Internal: the §3 no-public-netId
    // rule. The object registry maps netId -> this.
    internal int NetId;

    // The connection that owns this object's INPUT authority (the controlling client), or
    // Connection.None for a server-owned world/AI object. Set at spawn; changes via TransferOwnership
    // (P1). NotSerialized: ownership is runtime topology, never persisted to YAML.
    [NotSerialized]
    public Connection Owner { get; internal set; } = Connection.None;

    // True between OnSpawned and OnDespawned — "safe to touch networked members" (plan §4d / §8.5).
    // Independent of IsActive: a locally-disabled spawned object is still spawned (§8.5 disable rule).
    [NotSerialized]
    public bool IsSpawned { get; internal set; }

    // ---- authority (the two orthogonal axes, L3) --------------------------------------------------
    // P0: resolved from the topology + owner at spawn and cached. Server/host has State authority over
    // objects it spawns; the owning connection has Input authority. P1 promotes this to the full
    // per-machine truth-table; the API shape is final here so callers never change.
    [NotSerialized]
    public NetworkAuthority Authority { get; internal set; } = NetworkAuthority.None;

    public bool HasStateAuthority => (Authority & NetworkAuthority.State) != 0;
    public bool HasInputAuthority => (Authority & NetworkAuthority.Input) != 0;

    // IsProxy ≡ NEITHER authority (the §4d.1 host-corner: false on a host for everything, because the
    // host always has State authority). Precisely L3, not "I don't drive its input".
    public bool IsProxy => !HasStateAuthority && !HasInputAuthority;

    // IsOwner: this machine is the input authority / owning connection. In loopback the local host
    // owns its own objects, so this is trivially correct. Derived, never a stored third flag.
    public bool IsOwner => HasInputAuthority;

    public int OwnerId => Owner.Id;
}
