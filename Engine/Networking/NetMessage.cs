using BallisticEngine.Networking;

namespace BallisticEngine;

public enum NetMessage : byte {
    Handshake = 1,
    HandshakeOk = 2,
    Spawn = 3,
    Despawn = 4,
    Snapshot = 5,
    Rpc = 6,
    Input = 7,
    Ack = 8,
    Possess = 9,
    SceneState = 10,
    SceneAck = 11,
}
