using BallisticEngine.Networking;

namespace BallisticEngine;

[Component("Game State", "Gameplay")]
public class GameState : SceneBehaviour, IReplicated {
    public static GameState Active { get; private set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    public int ReplicationId { get; internal set; }

    public virtual bool IsDirty => false;
    public virtual void Serialize(BitWriter writer) { }
    public virtual void Deserialize(ref BitReader reader) { }
    public virtual void ClearDirty() { }

    public virtual void SerializeFull(BitWriter writer) { }

    public virtual void CaptureReplBaseline() { }

    public virtual int ReplicationTypeId => 0;
    public virtual int ReplicationLayoutHash => 0;
    public virtual bool HasReplicatedState => false;

    public virtual object __GetReplBaseline() => null;
    public virtual void __SetReplBaseline(object token) { }
    public virtual bool __ReplStateEquals(object token) => true;
}
