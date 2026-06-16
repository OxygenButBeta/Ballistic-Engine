using BallisticEngine.Networking;
using LiteNetLib;
using LiteNetLib.Utils;

namespace BallisticEngine.LiteNetLibBackend;

// The real-socket ITransport (plan §12.1 / P3) — the ONLY file allowed to reference LiteNetLib, exactly
// as Physics/Bepu quarantines BepuPhysics (grep-auditable per CLAUDE.md). Swapping this for Steam GNS or
// back to loopback never touches a line of snapshot/replication code — that's the whole point of the
// ITransport seam. The 2.1.4 API used here is PINNED + proven over a localhost socket in
// %TEMP%\bal-litenetlib-test (NetManager/NetPeer/EventBasedNetListener/DeliveryMethod/NetDataWriter).
//
// Reliability = the library's; wire format = ours (§12.1). Payloads are opaque ReadOnlySpan<byte> (the
// bit-packed delta-snapshot of §11 rides inside, the transport never inspects it). We map our two
// Channels onto LiteNetLib's delivery methods: Unreliable→Unreliable (snapshots, latest-wins),
// Reliable→ReliableOrdered (spawns/despawns/RPCs).
public sealed class LiteNetLibTransport : ITransport {
    // The handshake key — a peer that connects with the wrong key is rejected (a coarse gate; the real
    // ConnectionToken auth is §9.8 / P7). The layout-hash drift check rides ABOVE this, in NetworkManager.
    const string ConnectKey = "ballistic";

    readonly EventBasedNetListener listener = new();
    readonly NetManager net;
    readonly string host;
    readonly int port;
    readonly bool isServer;

    // NetPeer.Id is a stable int per the harness — we use it directly as our Connection.Id. Map both ways
    // so Send(Connection) can find the peer, and a receive/disconnect can surface our Connection.
    readonly Dictionary<int, NetPeer> peersById = new();

    LiteNetLibTransport(string host, int port, bool isServer) {
        this.host = host;
        this.port = port;
        this.isServer = isServer;
        net = new NetManager(listener) { AutoRecycle = true, UnconnectedMessagesEnabled = false };
        WireListener();
    }

    // A server bound to a port (accepts clients); a client that will Connect(host, port).
    public static LiteNetLibTransport Server(int port) => new(null, port, isServer: true);
    public static LiteNetLibTransport Client(string host, int port) => new(host, port, isServer: false);

    public bool IsRunning => net.IsRunning;

    public Action<Connection> OnConnected { get; set; }
    public Action<Connection> OnDisconnected { get; set; }
    public ReceiveHandler OnReceived { get; set; }

    void WireListener() {
        // Server: accept any peer with the right key. (A pure client never receives this event.)
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
            // Map the delivery method back to our Channel so the receiver knows the guarantee.
            Channel channel = deliveryMethod == DeliveryMethod.Unreliable ? Channel.Unreliable : Channel.Reliable;
            // GetRemainingBytes hands us the opaque payload (the snapshot path proven in the harness).
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
            net.Start();   // client binds an ephemeral local port
        NetPeer server = net.Connect(host, port, ConnectKey);
        if (server is not null)
            peersById[server.Id] = server;   // so a client can address the server by Connection
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
        // The library copies the buffer, so the caller may reuse `payload` after this (the ITransport
        // contract). NetDataWriter.Put(bytes,off,len) writes the raw opaque payload; the receiver reads
        // it back with GetRemainingBytes (proven byte-identical in the harness).
        var writer = new NetDataWriter();
        writer.Put(payload.ToArray(), 0, payload.Length);
        DeliveryMethod method = channel == Channel.Unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
        peer.Send(writer, method);
    }

    // Drain transport events for this frame (the once-per-tick pump, called by NetworkManager.Tick).
    public void Poll() {
        if (net.IsRunning)
            net.PollEvents();
    }
}
