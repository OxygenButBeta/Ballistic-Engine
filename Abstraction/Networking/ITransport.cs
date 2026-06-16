namespace BallisticEngine.Networking;

// Delivery guarantee for a packet. Maps onto the backend's channels (plan §12.1): the loopback
// transport ignores it (in-process), LiteNetLib maps Reliable→ReliableOrdered / Unreliable→Unreliable.
// Snapshots ride Unreliable (latest-wins); spawns/RPCs ride Reliable.
public enum Channel {
    Unreliable,   // state snapshots — loss-tolerant, latest-value-wins (plan L1)
    Reliable,     // spawns, despawns, RPCs — must arrive, in order
}

// A connected peer, identified by a STABLE id the transport assigns. NOT the persistent
// player identity (that's ConnectionToken / §9.8 — a P7 concern); this is the transport-level
// handle used to address sends. Id 0 is reserved for the local/loopback connection.
public readonly record struct Connection(int Id) {
    public static readonly Connection Local = new(0);
    public static readonly Connection None = new(-1);
    public bool IsValid => Id >= 0;
    public bool IsLocal => Id == 0;
    public override string ToString() => IsLocal ? "Connection(local)" : $"Connection({Id})";
}

// The ONLY thing the networking layer knows about the wire. Quarantines the backend (loopback,
// LiteNetLib, Steam GNS) exactly like IPhysicsWorld quarantines Bepu — swapping the backend never
// touches snapshot/replication code. Payloads are opaque ReadOnlySpan<byte> (the bit-packed
// delta-snapshot encoding of §11 rides inside, the transport never inspects it). NO snapshot
// semantics leak in here (plan §12.1).
//
// Lifecycle: the host calls StartServer()/Connect(); each frame the network tick calls Poll() to
// drain incoming events, then Send(...) for outgoing. Events surface through the callbacks.
public interface ITransport {
    // Server side: begin accepting connections. Loopback wires the local pair immediately.
    void StartServer();

    // Client side: connect to a server. Loopback connects to the in-process server.
    void Connect();

    // Tear down all connections and stop. Idempotent.
    void Stop();

    // True once StartServer/Connect has run and the transport is live.
    bool IsRunning { get; }

    // Send an opaque payload to one connection on a channel. The span is consumed synchronously
    // (the transport copies what it needs) so the caller may reuse the buffer after the call.
    void Send(Connection target, ReadOnlySpan<byte> payload, Channel channel);

    // Drain pending transport events for this frame, firing the callbacks below. Called once per
    // network tick. A decorator (SimulatedTransport) may hold packets here to inject latency.
    void Poll();

    // ---- events (set by NetworkManager before Start) -----------------------------------------
    // A peer connected (server side: a client joined; client side: connected to the server).
    Action<Connection> OnConnected { get; set; }
    // A peer disconnected (graceful or timeout).
    Action<Connection> OnDisconnected { get; set; }
    // A payload arrived from a peer on a channel. The span is valid only for the callback's
    // duration — copy anything you keep.
    ReceiveHandler OnReceived { get; set; }
}

// Receive callback. A delegate type (not Action<>) because ReadOnlySpan<byte> is a ref struct and
// can't be a generic type argument.
public delegate void ReceiveHandler(Connection source, ReadOnlySpan<byte> payload, Channel channel);
