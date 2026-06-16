namespace BallisticEngine.Networking;

// The carve-out from "everything entity-based replicates for free" (plan §10): GameState is the ONE
// type that replicates WITHOUT living on an entity. The network tick collects IReplicated
// scene-behaviours and serializes them like any networked object, but addressed by a small fixed
// id rather than a spawned NetworkObject. P0 defines the seam; P7 implements the collection/dispatch
// (the bespoke machinery §10 flags — today's SceneBehaviour has no tick).
//
// A type implementing this exposes its replicated state through the same BitWriter/BitReader the
// source generator (P2) drives for NetworkBehaviour; until codegen lands, P0 leaves the methods for
// the implementer (GameState) to hand-fill or to be generated.
public interface IReplicated {
    // Stable per-scene id so both ends address the same IReplicated object without a spawn handshake.
    // Assigned by the network tick from a deterministic order (e.g. registration order). Read-only to
    // game code.
    int ReplicationId { get; }

    // True when any [Networked] member changed since the last send (the dirty bit §11 uses to skip
    // unchanged objects at ~1 bit). P0: implementers may return true (always send) until delta lands.
    bool IsDirty { get; }

    // Pack the replicated state. Mirrors NetworkBehaviour.SerializeState; the generator will emit this.
    void Serialize(BitWriter writer);

    // Apply received state. Mirrors DeserializeState.
    void Deserialize(ref BitReader reader);

    // Clear the dirty flag after a successful send (called by the network tick).
    void ClearDirty();
}
