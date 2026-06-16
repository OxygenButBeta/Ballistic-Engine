using BallisticEngine.Networking;

namespace BallisticEngine;

// Replicated match state (plan §2 / §10) — score, round timer, phase. A SceneBehaviour (scene-wide)
// + IReplicated: the ONE type that replicates WITHOUT being on an entity (the §10 carve-out). The
// network tick collects IReplicated scene-behaviours and serializes them by ReplicationId, no spawn
// handshake. A late joiner receives the current GameState (the §5 global-state delivery).
//
// P7 (DELIVERED): a GameState subclass declares `[Networked]` auto-properties and is `partial`; the
// SOURCE GENERATOR (the SAME one that targets NetworkBehaviour, extended to IReplicated SceneBehaviours
// in P7) emits Serialize/Deserialize/SerializeFull/CaptureBaseline + the per-client baseline-swap trio
// (__GetReplBaseline/__SetReplBaseline/__ReplStateEquals) + IsDirty/ClearDirty + ReplicationTypeId/
// LayoutHash + a [ModuleInitializer] registering into SceneReplicationRegistry — exactly mirroring the
// NetworkBehaviour path (the §10 "bespoke but not free" machinery, now made declarative). A GameState
// with NO [Networked] members ships nothing (the base no-ops below — the generator only touches a type
// that declares replicated state, §11 scoping).
//
// The network tick (NetworkManager) collects every active IReplicated SceneBehaviour into the SAME
// per-client delta flush the entity path uses, keyed by ReplicationId instead of a spawned netId (a
// SEPARATE id space so a netId can never collide with a ReplicationId — ClientReplState.SceneBaseline).
// Proven entity-less in %TEMP%\bal-scenestate-test (21/21) before this integration.
[Component("Game State", "Gameplay")]
public class GameState : SceneBehaviour, IReplicated {
    public static GameState Active { get; private set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    // A fixed per-scene replication id. P7: assigned by the network tick from the deterministic
    // registration order of the active IReplicated SceneBehaviours (NetworkManager.AssignReplicationIds).
    // A single GameState ⇒ id 0; a 2nd IReplicated scene-behaviour gets id 1, etc. Read-only to game code.
    public int ReplicationId { get; internal set; }

    // ---- the IReplicated surface (the source generator OVERRIDES these for a [Networked] subclass) ----
    // IsDirty: any [Networked] member changed since the last baseline. The generator emits a real diff;
    // the base (no [Networked]) is never dirty so a bare GameState ships nothing. Serialize/Deserialize
    // are the delta path; SerializeFull is the spawn/join baseline (every field). ClearDirty is folded
    // into CaptureBaseline (the next-baseline snapshot) — kept on the interface for the §2 seam shape.
    public virtual bool IsDirty => false;
    public virtual void Serialize(BitWriter writer) { }
    public virtual void Deserialize(ref BitReader reader) { }
    public virtual void ClearDirty() { }

    // P7: the spawn/late-join FULL snapshot (every [Networked] field) — the §8.5 "baseline delivered
    // atomically" for the entity-less path. Generated; base no-op.
    public virtual void SerializeFull(BitWriter writer) { }

    // P7: capture the live [Networked] values as the next delta baseline (the per-client send + late-join
    // seed snapshot it against). Generated; base no-op.
    public virtual void CaptureReplBaseline() { }

    // P7: the wire identity + drift guard (mirrors NetworkBehaviour.NetworkTypeId/LayoutHash). The handshake
    // layout digest folds these in so a drifted GameState layout is an explicit error, not a silent desync
    // (gate 0c, §8.6.1). 0 on a bare GameState (it carries no state).
    public virtual int ReplicationTypeId => 0;
    public virtual int ReplicationLayoutHash => 0;
    public virtual bool HasReplicatedState => false;

    // P7 PER-CLIENT BASELINE swap (plan §13 late-join) — IDENTICAL mechanism to NetworkBehaviour's
    // __GetNetBaseline/__SetNetBaseline/__NetStateEquals, but for the entity-less IReplicated path. The
    // network tick swaps in each client's last-acked baseline around the per-client Serialize so each
    // client gets exactly the deltas since ITS ack (the staggered-join correctness, re-proven for the
    // id-keyed path in %TEMP%\bal-scenestate-test). The token is the generated baseline struct, BOXED —
    // on the 20 Hz send path, NOT the per-tick hot path, no reflection. Base returns/accepts null.
    public virtual object __GetReplBaseline() => null;
    public virtual void __SetReplBaseline(object token) { }
    public virtual bool __ReplStateEquals(object token) => true;
}
