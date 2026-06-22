namespace BallisticEngine.Networking;

public enum NetworkWriteAuthority {
    Server,
    Owner,
}

public enum RpcTarget {
    Server,
    Owner,
    All,
}

public interface INetworkSerializable {
    int NetworkTypeId { get; }

    int NetworkLayoutHash { get; }

    void SerializeState(BitWriter writer);

    void DeserializeState(ref BitReader reader);

    void CaptureBaseline();
}
