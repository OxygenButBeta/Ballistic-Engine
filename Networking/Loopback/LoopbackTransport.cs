using BallisticEngine.Networking;

namespace BallisticEngine.Loopback;

// In-process transport (plan §12.1 / D5): server and client are the SAME process, so a Send just
// queues a payload that Poll delivers back. Single-player and `bal simulate` run on this — the exact
// same NetworkManager code path as multiplayer; only the transport collapses. BCL-only, no socket.
//
// Determinism: no wall-clock, no randomness — a Send enqueues, the next Poll delivers in FIFO order.
// (Latency/loss/jitter is the SimulatedTransport decorator's job, not baked in here — §8.3.)
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
        // The in-process client "connects" instantly: both halves see the local connection. Deferred to
        // the first Poll so the NetworkManager has wired its callbacks before the event fires.
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
        // Loopback: the only peer is the local connection; deliver the copy back through Poll. Source
        // is "the other half" — modelled as Local since it's one process (P3's real transport carries
        // a distinct remote id).
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
