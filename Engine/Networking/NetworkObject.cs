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

    // CLIENT side, AUTONOMOUS PROXY only (P5d, plan §13): the visible-correction smoother. P5b's reconcile
    // SNAPS the transform to the authoritative+replayed pose; when the prediction was WRONG (a
    // misprediction), that snap is a visible pop. The smoother carries a decaying render OFFSET so the
    // correction eases in over a few frames instead of popping. Lazily created on the first correction.
    [NotSerialized]
    internal PredictionSmoother Smoother { get; set; }

    // Observability (P5d): true while a misprediction correction is being eased in (a non-zero smoothing
    // offset is decaying). Tools/tests read this; the value is the smoother's active flag.
    public bool IsSmoothingCorrection => Smoother is { IsActive: true };

    // ---- predicted spawn (P5f, §8.5.1) ------------------------------------------------------------
    // CLIENT side: the spawn-prediction KEY this object was predicted under (0 = a normal server spawn,
    // not predicted). Set by Network.PredictSpawn; cleared to 0 when the authoritative spawn LINKS to it
    // (confirm) — the §8.5.1 reconcile-link. While non-zero the object is a predicted-but-unconfirmed copy
    // with no server baseline (the one place "OnSpawned == baseline delivered" does NOT hold).
    [NotSerialized]
    public uint PredictKey { get; internal set; }

    // CLIENT side: the tick by which this predicted spawn must be confirmed by an authoritative spawn, or
    // it is ROLLED BACK (destroyed). Set by Network.PredictSpawn. 0 = not a pending prediction.
    [NotSerialized]
    internal long PredictConfirmDeadline { get; set; }

    public bool IsPredictedSpawn => PredictKey != 0;

    // ---- lag compensation (P8a, §9 item 9 / §13) --------------------------------------------------
    // SERVER side: the hitbox radius this object presents to lag-compensated raycasts. 0 (default) = NOT
    // lag-comp-tracked — most objects don't need it; a player pawn opts in (set it on spawn). When > 0 the
    // network tick records this object's pose into PoseHistory each tick, and a lag-compensated shot can
    // rewind it to a past tick (favor-the-shooter, §9.9). A sphere hitbox keeps the rewind/restore exact +
    // headless-testable (real hitscan against a dedicated hitbox, decoupled from the Bepu world that only
    // syncs at fixed-step boundaries and needs GL); a capsule/box refinement is a later extension.
    [NotSerialized]
    public float LagHitboxRadius { get; set; }

    public bool IsLagCompensated => LagHitboxRadius > 0f;

    // SERVER side: the ring of historical poses (one per tick) this object's hitbox is rewound against. Lazily
    // created when the object is first recorded (only for IsLagCompensated objects). null on a client / an
    // untracked object — the rewind only ever runs on the server (the authority that resolves hits).
    [NotSerialized]
    internal PoseHistory LagHistory { get; set; }

    // Observability (P8a): the number of historical poses currently buffered for this object's hitbox — 0
    // when untracked or not yet recorded. Tools/tests read it to confirm the ring is accruing; the rewind
    // itself is server-internal.
    public int LagHistoryCount => LagHistory?.Count ?? 0;
}
