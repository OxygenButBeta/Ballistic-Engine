using BallisticEngine.Networking;

namespace BallisticEngine.Loopback;

public sealed class LoopbackTransport : ITransport {
    readonly Queue<Packet> inbox = new();
    bool serverUp, clientUp;

    readonly record struct Packet(Connection Source, byte[] Payload, Channel Channel);

    public bool IsRunning => serverUp || clientUp;

    public Action<Connection> OnConnected { get; set; }
    public Action<Connection> OnDisconnected { get; set; }
    public ReceiveHandler OnReceived { get; set; }

    public void StartServer() {
        serverUp = true;
    }

    public void Connect() {
        clientUp = true;
        pendingConnect = true;
    }

    bool pendingConnect;

    public void Stop() {
        if (IsRunning && (serverUp && clientUp))
            OnDisconnected?.Invoke(Connection.Local);
        serverUp = clientUp = false;
        pendingConnect = false;
        inbox.Clear();
    }

    public void Send(Connection target, ReadOnlySpan<byte> payload, Channel channel) {
        if (!IsRunning)
            return;
        inbox.Enqueue(new Packet(Connection.Local, payload.ToArray(), channel));
    }

    public void Poll() {
        if (pendingConnect) {
            pendingConnect = false;
            OnConnected?.Invoke(Connection.Local);
        }
        while (inbox.Count > 0) {
            Packet p = inbox.Dequeue();
            OnReceived?.Invoke(p.Source, p.Payload, p.Channel);
        }
    }
}
