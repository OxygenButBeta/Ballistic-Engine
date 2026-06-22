namespace BallisticEngine.Networking;

public interface INetworkSerializable {
    int NetworkTypeId { get; }

    int NetworkLayoutHash { get; }

    void SerializeState(BitWriter writer);

    void DeserializeState(ref BitReader reader);

    void CaptureBaseline();
}
