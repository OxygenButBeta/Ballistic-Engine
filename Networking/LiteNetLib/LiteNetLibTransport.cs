using BallisticEngine.Networking;
using LiteNetLib;
using LiteNetLib.Utils;

namespace BallisticEngine.LiteNetLibBackend;

public sealed class LiteNetLibTransport : ITransport {
    const string ConnectKey = "ballistic";

    readonly EventBasedNetListener listener = new();
    readonly NetManager net;
    readonly string host;
    readonly int port;
    readonly bool isServer;

    readonly Dictionary<int, NetPeer> peersById = new();

    LiteNetLibTransport(string host, int port, bool isServer) {
        this.host = host;
        this.port = port;
        this.isServer = isServer;
        net = new NetManager(listener) { AutoRecycle = true, UnconnectedMessagesEnabled = false };
        WireListener();
    }

    public static LiteNetLibTransport Server(int port) => new(null, port, isServer: true);
    public static LiteNetLibTransport Client(string host, int port) => new(host, port, isServer: false);

    public bool IsRunning => net.IsRunning;

    public Action<Connection> OnConnected { get; set; }
    public Action<Connection> OnDisconnected { get; set; }
    public ReceiveHandler OnReceived { get; set; }

    void WireListener() {
        listener.ConnectionRequestEvent += request => {
            if (isServer) request.AcceptIfKey(ConnectKey);
            else request.Reject();
        };
        listener.PeerConnectedEvent += peer => {
            peersById[peer.Id] = peer;
            OnConnected?.Invoke(new Connection(peer.Id));
        };
        listener.PeerDisconnectedEvent += (peer, info) => {
            peersById.Remove(peer.Id);
            OnDisconnected?.Invoke(new Connection(peer.Id));
        };
        listener.NetworkReceiveEvent += (peer, reader, channelNumber, deliveryMethod) => {
            Channel channel = deliveryMethod == DeliveryMethod.Unreliable ? Channel.Unreliable : Channel.Reliable;
            byte[] payload = reader.GetRemainingBytes();
            reader.Recycle();
            OnReceived?.Invoke(new Connection(peer.Id), payload, channel);
        };
    }

    public void StartServer() {
        if (net.IsRunning) return;
        if (!net.Start(port))
            Debugging.LogError($"LiteNetLibTransport: server failed to bind port {port}.");
    }

    public void Connect() {
        if (!net.IsRunning)
            net.Start();
        NetPeer server = net.Connect(host, port, ConnectKey);
        if (server is not null)
            peersById[server.Id] = server;
    }

    public void Stop() {
        if (net.IsRunning) {
            net.DisconnectAll();
            net.Stop();
        }
        peersById.Clear();
    }

    public void Send(Connection target, ReadOnlySpan<byte> payload, Channel channel) {
        if (!net.IsRunning)
            return;
        if (!peersById.TryGetValue(target.Id, out NetPeer peer)) {
            Debugging.LogWarning($"LiteNetLibTransport: no peer for {target} — send dropped.");
            return;
        }

        var writer = new NetDataWriter();
        writer.Put(payload.ToArray(), 0, payload.Length);
        DeliveryMethod method = channel == Channel.Unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
        peer.Send(writer, method);
    }

    public void Poll() {
        if (net.IsRunning)
            net.PollEvents();
    }
}
