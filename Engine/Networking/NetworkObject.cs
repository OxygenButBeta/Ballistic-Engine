using BallisticEngine.Networking;

namespace BallisticEngine;

// Net identity + authority/ownership holder (plan §2) — the UNIT that spawns/despawns; the
// [Networked] state of every NetworkBehaviour on the same entity lives under it. A Behaviour, so the
// editor/serializer/registry discover it free (§10).
//
// Authority is the full §4d.1 truth-table (P1): resolved per-machine from (topology, localConnection,
// owner) by NetworkManager.ResolveAuthority — the same function every machine runs with its own inputs,
// so roles are correct on a dedicated server, a host, the owning client, and a watching client alike.
//
// netId is INTERNAL — never a public field (§3). Game code addresses objects by a generational
// NetworkRef<T> handle (§8.4) that nulls on despawn. The netId packs (slot, generation).
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

    // ---- authority (the two orthogonal axes, L3 / the §4d.1 truth-table) --------------------------
    // Resolved per-machine at spawn (and on TransferOwnership) by NetworkManager.ResolveAuthority and
    // cached. Never collapsed into one flag — that collapse is the root of the "who runs this code" /
    // "IsOwner on host" edge-case class.
    [NotSerialized]
    public NetworkAuthority Authority { get; internal set; } = NetworkAuthority.None;

    public bool HasStateAuthority => (Authority & NetworkAuthority.State) != 0;
    public bool HasInputAuthority => (Authority & NetworkAuthority.Input) != 0;

    // IsProxy ≡ NEITHER authority (the §4d.1 host-corner: false on a host for everything, because the
    // host always has State authority). Precisely L3, not "I don't drive its input".
    public bool IsProxy => !HasStateAuthority && !HasInputAuthority;

    // The two PROXY KINDS the table distinguishes (the corner the earlier draft left undefined):
    //   AutonomousProxy — the owning client: predicts + reads input, but the server owns truth.
    //                     !State && Input. (Not a proxy in the IsProxy sense — it has Input authority.)
    //   SimulatedProxy  — a watching client: neither authority, interpolated. Exactly IsProxy.
    public bool IsAutonomousProxy => !HasStateAuthority && HasInputAuthority;
    public bool IsSimulatedProxy => IsProxy;

    // IsOwner: this machine is the input authority / owning connection. Derived, never a stored third
    // flag (FishNet's host trap, designed out — on a host this is correct because Owner == LocalConn).
    public bool IsOwner => HasInputAuthority;

    public int OwnerId => Owner.Id;

    // ---- prediction/reconcile (P5b, plan §8.2) ----------------------------------------------------
    // SERVER side: the queue of per-tick inputs received from the owning client (the UP stream), awaiting
    // authoritative apply — one input consumed per fixed tick. NotSerialized: runtime-only, server-only.
    // null on a client / a server-owned object (no remote input source). Lazily created on first input.
    [NotSerialized]
    internal Queue<BallisticEngine.Networking.NetworkInput> ServerInputInbox { get; set; }

    // SERVER side: the highest input seq this object has authoritatively processed — stamped into the
    // state snapshot DOWN so the owning client can TRIM acked inputs + REPLAY the rest (the reconcile).
    // CLIENT side: the last-processed-seq received from the server (the ack frontier for AckThrough).
    // Public getter (observability — the ack frontier a tool/test reads), framework-only setter.
    [NotSerialized]
    public uint LastProcessedSeq { get; internal set; }

    // SERVER side: the last input actually applied — re-applied (extrapolated) on an input-starved tick so
    // the authoritative sim keeps moving rather than freezing (plan §8.2; the client's replay stays the
    // authority on the unacked window). Valid once HaveLastServerInput is true.
    [NotSerialized]
    internal BallisticEngine.Networking.NetworkInput LastServerInput { get; set; }
    [NotSerialized]
    internal bool HaveLastServerInput { get; set; }

    // CLIENT side, SimulatedProxy only (P5c, plan §13): the pose-interpolation buffer. A proxy is neither
    // authority — it does NOT simulate locally; it renders the remote pose ~InterpDelay ticks in the past,
    // lerping between received snapshots (smooth under loss/jitter). Lazily created on the first snapshot
    // for a proxy object; null for an authority/owner object (which simulates, never interpolates).
    [NotSerialized]
    internal SnapshotInterpolator Interpolator { get; set; }

    // CLIENT side: the proxy's own monotonic interpolation clock (advanced once per fixed tick) — the
    // time axis the interpolator renders in the past against. Independent of the server tick (a local
    // render clock), so it stays smooth regardless of when snapshots actually arrive.
    [NotSerialized]
    internal double InterpClock { get; set; }
}
