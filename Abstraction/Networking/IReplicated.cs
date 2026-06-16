namespace BallisticEngine.Networking;

// The carve-out from "everything entity-based replicates for free" (plan §10): GameState is the ONE
// type that replicates WITHOUT living on an entity. The network tick collects IReplicated
// scene-behaviours and serializes them like any networked object, but addressed by a small fixed
// ReplicationId rather than a spawned NetworkObject. P0 defined the seam; P7 implements the
// collection/dispatch (the bespoke machinery §10 flags — today's SceneBehaviour has no tick).
//
// P7: a GameState subclass with [Networked] members gets these implemented by the SAME source generator
// that targets NetworkBehaviour (extended to IReplicated SceneBehaviours). The network tick collects
// every active IReplicated, swaps in each client's last-acked baseline around Serialize (the per-client
// delta), and dispatches each received block by ReplicationId. Proven entity-less in
// %TEMP%\bal-scenestate-test before integration. The wire shape mirrors NetworkBehaviour exactly, so the
// per-client baseline + the layout-digest drift guard carry over unchanged.
public interface IReplicated {
    // Stable per-scene id so both ends address the same IReplicated object without a spawn handshake.
    // Assigned by the network tick from a deterministic order (registration order). Read-only to game code.
    int ReplicationId { get; }

    // True when any [Networked] member changed since the last baseline (the dirty bit §11 uses to skip
    // unchanged objects). The generator emits a real diff; a bare IReplicated is never dirty.
    bool IsDirty { get; }

    // Pack the replicated state DELTA (changemask + only-changed fields vs the current baseline). Mirrors
    // NetworkBehaviour.SerializeState; the generator emits it.
    void Serialize(BitWriter writer);

    // Apply a received DELTA (read changemask + changed fields). Mirrors DeserializeState.
    void Deserialize(ref BitReader reader);

    // Clear the dirty flag after a successful send. Folded into the baseline capture; kept for the seam.
    void ClearDirty();

    // P7 — the wire identity + drift guard (mirrors NetworkBehaviour.NetworkTypeId/LayoutHash). The
    // handshake layout digest folds these in (gate 0c, §8.6.1). 0 on a bare IReplicated.
    int ReplicationTypeId { get; }
    int ReplicationLayoutHash { get; }

    // P7 — true when the type carries [Networked] state (lets the tick skip a bare IReplicated).
    bool HasReplicatedState { get; }

    // P7 — the spawn / late-join FULL snapshot (every field, no diff): the §8.5 "baseline delivered
    // atomically" for the entity-less path. The generator emits it; a bare IReplicated no-ops.
    void SerializeFull(BitWriter writer);

    // P7 — capture the live [Networked] values as the next delta baseline (the per-client send + late-join
    // seed snapshot it against). Generated; base no-op.
    void CaptureReplBaseline();

    // P7 PER-CLIENT BASELINE swap (plan §13 late-join) — IDENTICAL mechanism to NetworkBehaviour's
    // __GetNetBaseline / __SetNetBaseline / __NetStateEquals, but for the entity-less path. The network
    // tick swaps in each client's last-acked baseline around the per-client Serialize so each client gets
    // exactly the deltas since ITS ack. The token is the generated baseline struct, BOXED — on the send
    // path, not the per-tick hot path, no reflection. Base returns/accepts null.
    object __GetReplBaseline();
    void __SetReplBaseline(object token);
    bool __ReplStateEquals(object token);
}
