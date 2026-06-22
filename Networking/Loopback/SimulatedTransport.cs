using BallisticEngine.Networking;

namespace BallisticEngine.Loopback;

public sealed class SimulatedTransport : ITransport {
    readonly ITransport inner;
    readonly System.Random rng;
    readonly SimSettings settings;

    long tick;
    long sequence;

    readonly List<Held> held = new();

    readonly record struct Held(long ReleaseTick, long Seq, Connection Target, byte[] Payload, Channel Channel);

    public SimulatedTransport(ITransport inner, SimSettings settings, System.Random rng = null) {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.settings = settings;
        this.rng = rng ?? new System.Random(12345);
        this.inner.OnConnected = c => OnConnected?.Invoke(c);
        this.inner.OnDisconnected = c => OnDisconnected?.Invoke(c);
        this.inner.OnReceived = (s, p, ch) => OnReceived?.Invoke(s, p, ch);
    }

    public bool IsRunning => inner.IsRunning;
    public Action<Connection> OnConnected { get; set; }
    public Action<Connection> OnDisconnected { get; set; }
    public ReceiveHandler OnReceived { get; set; }

    public void StartServer() => inner.StartServer();
    public void Connect() => inner.Connect();

    public void Stop() {
        held.Clear();
        inner.Stop();
    }

    public void AdvanceTick() {
        tick++;
        if (held.Count == 0)
            return;
        held.Sort(static (a, b) =>
            a.ReleaseTick != b.ReleaseTick ? a.ReleaseTick.CompareTo(b.ReleaseTick) : a.Seq.CompareTo(b.Seq));
        int i = 0;
        while (i < held.Count && held[i].ReleaseTick <= tick) {
            Held h = held[i];
            inner.Send(h.Target, h.Payload, h.Channel);
            held.RemoveAt(i);
        }
    }

    public void Send(Connection target, ReadOnlySpan<byte> payload, Channel channel) {
        if (channel == Channel.Unreliable && settings.LossFraction > 0 && rng.NextDouble() < settings.LossFraction)
            return;

        int latency = settings.LatencyTicks;
        if (settings.JitterTicks > 0)
            latency += rng.Next(-settings.JitterTicks, settings.JitterTicks + 1);
        latency = Math.Max(0, latency);

        long release = tick + latency;
        held.Add(new Held(release, sequence++, target, payload.ToArray(), channel));
        if (settings is { LatencyTicks: 0, JitterTicks: 0 } && channel == Channel.Reliable) {
            Held h = held[^1];
            held.RemoveAt(held.Count - 1);
            inner.Send(h.Target, h.Payload, h.Channel);
        }
    }

    public void Poll() => inner.Poll();
}
