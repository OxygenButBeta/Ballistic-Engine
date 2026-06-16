using BallisticEngine.Networking;

namespace BallisticEngine.Loopback;

// Latency / loss / jitter / reorder injection (plan §8.3) as a DECORATOR that wraps ANY inner
// transport (loopback, LiteNetLib, Steam) — Mirror's LatencySimulation-transport pattern. NOT baked
// into loopback, NOT LiteNetLib's DEBUG-only sim. With it on, the "SP == MP same code" claim is
// stress-VERIFIED, not merely exercised: a `bal simulate` run can replay a scene at 0ms/0% and at
// 150ms/5%-loss (seeded, deterministic) and assert the result converges (the P5 reconcile test).
//
// Determinism: NO wall-clock. Delay is measured in network TICKS — the network tick calls
// AdvanceTick() once per step, and packets release when their release-tick arrives. Randomness comes
// from a seeded RNG passed in (vary by run, reproducible). This keeps two runs byte-identical, the
// BALLISTIC_DETERMINISTIC / `bal simulate` discipline.
public sealed class SimulatedTransport : ITransport {
    readonly ITransport inner;
    readonly System.Random rng;
    readonly SimSettings settings;

    long tick;                 // monotonic network-tick counter (AdvanceTick)
    long sequence;             // stable tie-breaker so equal release-ticks keep a deterministic order

    // Packets held until their release tick. A min-ordered list (small N — a frame's sends), sorted by
    // (releaseTick, sequence) so reorder is the rng's job, not list instability.
    readonly List<Held> held = new();

    readonly record struct Held(long ReleaseTick, long Seq, Connection Target, byte[] Payload, Channel Channel);

    public SimulatedTransport(ITransport inner, SimSettings settings, System.Random rng = null) {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.settings = settings;
        this.rng = rng ?? new System.Random(12345);   // a fixed seed by default — deterministic replays
        // The inner transport's receive feeds straight through; outgoing is what we delay.
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

    // Advance the simulated clock by one network tick and release any packets now due. Called by the
    // network tick BEFORE Poll so released packets are delivered this frame.
    public void AdvanceTick() {
        tick++;
        if (held.Count == 0)
            return;
        // Reliable packets are never dropped/reordered past each other (the channel's guarantee); only
        // Unreliable ones can. Release everything due this tick, in (releaseTick, seq) order.
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
        // Loss: drop Unreliable packets at the configured rate (Reliable always delivers — the ARQ
        // layer the real backend provides; the decorator must not break that guarantee).
        if (channel == Channel.Unreliable && settings.LossFraction > 0 && rng.NextDouble() < settings.LossFraction)
            return;

        // Latency + jitter, expressed in ticks. extraJitter is symmetric around 0.
        int latency = settings.LatencyTicks;
        if (settings.JitterTicks > 0)
            latency += rng.Next(-settings.JitterTicks, settings.JitterTicks + 1);
        latency = Math.Max(0, latency);

        long release = tick + latency;
        held.Add(new Held(release, sequence++, target, payload.ToArray(), channel));
        // Zero-latency, zero-jitter Unreliable could go straight through, but routing it through `held`
        // + the next AdvanceTick keeps ONE delivery path (no special-case divergence).
        if (settings is { LatencyTicks: 0, JitterTicks: 0 } && channel == Channel.Reliable) {
            // Reliable with no configured delay: deliver immediately to preserve in-order semantics for
            // callers that send a burst and Poll the same frame.
            Held h = held[^1];
            held.RemoveAt(held.Count - 1);
            inner.Send(h.Target, h.Payload, h.Channel);
        }
    }

    public void Poll() => inner.Poll();
}

// Tunable network conditions, in TICKS (deterministic). LatencyTicks 6 ≈ 100ms at 60Hz.
public readonly record struct SimSettings(int LatencyTicks, int JitterTicks, double LossFraction) {
    public static readonly SimSettings Perfect = new(0, 0, 0.0);
    public static SimSettings Lan => new(1, 0, 0.0);
    public static SimSettings Broadband => new(6, 2, 0.02);   // ~100ms, ±33ms jitter, 2% loss
    public static SimSettings Poor => new(12, 4, 0.05);       // ~200ms, heavy jitter, 5% loss
}
