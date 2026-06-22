namespace BallisticEngine.Networking;

public enum Channel {
    Unreliable,
    Reliable,
}

public readonly record struct Connection(int Id) {
    public static readonly Connection Local = new(0);
    public static readonly Connection None = new(-1);
    public bool IsValid => Id >= 0;
    public bool IsLocal => Id == 0;
    public override string ToString() => IsLocal ? "Connection(local)" : $"Connection({Id})";
}

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
