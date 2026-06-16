using BallisticEngine.Networking;

namespace BallisticEngine;

// Replicated match state (plan §2 / §10) — score, round timer, phase. A SceneBehaviour (scene-wide)
// + IReplicated: the ONE type that replicates WITHOUT being on an entity (the §10 carve-out). The
// network tick collects IReplicated scene-behaviours and serializes them by ReplicationId, no spawn
// handshake. A late joiner receives the current GameState (the §5 global-state delivery).
//
// P0 = the type + the IReplicated seam (default-dirty hand-fill). The collection/dispatch into the
// network tick + the source-generated Serialize/Deserialize land in P2/P7 (the bespoke machinery §10
// flags — today's SceneBehaviour has no tick).
[Component("Game State", "Gameplay")]
public class GameState : SceneBehaviour, IReplicated {
    public static GameState Active { get; private set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    // A fixed per-scene replication id (single GameState ⇒ slot 0). Multiple IReplicated scene
    // behaviours get distinct ids from the network tick's deterministic registration order (P7).
    public int ReplicationId { get; internal set; }

    // P0: always-send until delta encoding lands (P2). Subclasses with [Networked] members get a
    // generated dirty flag then.
    public virtual bool IsDirty => true;

    // The source generator (P2) emits these over [Networked] members. P0: no-op so the seam compiles
    // and a GameState with no networked state round-trips as empty.
    public virtual void Serialize(BitWriter writer) { }
    public virtual void Deserialize(ref BitReader reader) { }
    public virtual void ClearDirty() { }
}
