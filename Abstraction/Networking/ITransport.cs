namespace BallisticEngine.Networking;

public interface ITransport {
    void StartServer();

    void Connect();

    void Stop();

    bool IsRunning { get; }

    void Send(Connection target, ReadOnlySpan<byte> payload, Channel channel);

    void Poll();

    Action<Connection> OnConnected { get; set; }

    Action<Connection> OnDisconnected { get; set; }

    ReceiveHandler OnReceived { get; set; }
}
public delegate void ReceiveHandler(Connection source, ReadOnlySpan<byte> payload, Channel channel);
