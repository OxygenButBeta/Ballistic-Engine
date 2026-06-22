namespace BallisticEngine.Networking;

public interface IReplicated {
    int ReplicationId { get; }

    bool IsDirty { get; }

    void Serialize(BitWriter writer);

    void Deserialize(ref BitReader reader);

    void ClearDirty();

    int ReplicationTypeId { get; }
    int ReplicationLayoutHash { get; }

    bool HasReplicatedState { get; }

    void SerializeFull(BitWriter writer);

    void CaptureReplBaseline();

    object __GetReplBaseline();
    void __SetReplBaseline(object token);
    bool __ReplStateEquals(object token);
}
