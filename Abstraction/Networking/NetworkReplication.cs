namespace BallisticEngine.Networking;

// Replication-layer enums + interfaces the source generator (BallisticEngine.SourceGen) targets (plan
// §11). BCL-only — they live in Abstraction/Networking alongside NetworkRole/BitWriter so the generated
// code (which lands in the engine + game assemblies) and the wire primitives share one home, and the
// DX12 OpenTK-removal never touches them.

// WHO may write a [Networked] field (plan §4b / L4). Server (default — the closed trust boundary: a
// client cannot replicate a cheated value, no API path) or Owner (the loud opt-in for owner-predicted
// state like an aim vector). Distinct from the per-MACHINE NetworkAuthority (State/Input) — this is the
// DECLARED write policy of one field, resolved against the live authority at write time.
public enum NetworkWriteAuthority {
    Server,   // server-write / everyone-read (default, closed)
    Owner,    // owner-write — the visible, loud token (§3 Grade-2)
}

// The typed RPC target (plan §4b). Reliable by default (on RpcAttribute); To.Server owner-checked by
// default. No To.Self — a local call is a method call, not an RPC.
public enum RpcTarget {
    Server,   // client → server (owner-checked by default — the closed trust boundary)
    Owner,    // server → the owning client only
    All,      // server → every observing client
}

// The contract every generated NetworkBehaviour partial implements (plan §11). The network tick calls
// SerializeState on the state authority (write dirty fields vs the last ACK baseline) and DeserializeState
// on receivers (apply the changemask). NO reflection: the generator emits a concrete body per type, and
// the registration table (NetworkReplicationRegistry) maps a typeId to a factory that produces these.
//
// Baseline is passed as an opaque object the type knows how to interpret as ITS OWN snapshot struct
// (boxing is avoided in the hot path by the generator caching the snapshot on the component — see the
// engine NetworkBehaviour additions). The interface keeps the registry table type-agnostic.
public interface INetworkSerializable {
    // Stable integer identity of this concrete type (FNV of the full type name) — the wire's typeId.
    int NetworkTypeId { get; }

    // Hash of the [Networked] field LAYOUT (name|wireKind|quantize per field). Stamped into the wire
    // handshake so a peer on a drifted build is rejected with an explicit error, not a silent desync
    // (gate 0c / §8.6.1). Accident-detection only — it cannot see NetworkTick logic drift.
    int NetworkLayoutHash { get; }

    // Write the changemask + only-changed fields vs the captured baseline (delta, §11). On a full send
    // (late-join / first snapshot) the caller passes a zero baseline so every field ships.
    void SerializeState(BitWriter writer);

    // Read a changemask + apply only the changed fields (clear bits keep the current value).
    void DeserializeState(ref BitReader reader);

    // Capture the current [Networked] values as THIS object's delta baseline (the last-ACK snapshot the
    // next SerializeState diffs against). Called by the network tick after a successful send/ack.
    void CaptureBaseline();
}
