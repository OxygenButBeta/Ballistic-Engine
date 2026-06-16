namespace BallisticEngine;

// ASYMMETRIC SEND-RATE (plan §8.2 / §14 item 3) — the non-obvious P2 decision that, conflated, is a
// FUNCTIONAL bug in P5a/P5b (not a tuning miss). PROVEN in %TEMP%\bal-netserde-test; this is the engine
// seam the network tick rides. We do NOT build a second clock (L2) — the divisor brackets the EXISTING
// 60 Hz fixed step.
//
//   - State DOWN (server→client): low send-rate. Simulate 60 Hz, REPLICATE every Divisor-th tick
//     (default 3 → 20 Hz) + interpolate on the receiver. The divisor caps bandwidth.
//   - Input  UP  (client→server): MUST be per-tick (60 Hz), BATCHED. The server simulates authoritatively
//     every tick → it needs EVERY tick's input. Input only at the down-rate ⇒ the server is input-STARVED.
//     A client sends ~Divisor ticks' input in ONE packet at the send cadence, but NEVER drops a tick.
//
// The §13 P2 scope wires the DOWN state path through NetworkManager.Tick over loopback; the UP input
// stream + the full reconcile loop are P5 — but the seam is asymmetric FROM THE START so P5 can't inherit
// a conflated rate (which is exactly the §14-item-3 functional bug).
public sealed class SendRateClock {
    public const int DefaultDivisor = 3;   // 60 Hz sim / 3 = 20 Hz down-send (Fusion/FishNet-class default)

    public int Divisor { get; }
    public int LocalTick { get; private set; }

    public SendRateClock(int divisor = DefaultDivisor) {
        if (divisor < 1) throw new ArgumentOutOfRangeException(nameof(divisor), "send-rate divisor must be >= 1");
        Divisor = divisor;
    }

    // Advance one fixed tick. Returns whether a SEND boundary falls on this tick — the down-state flush
    // AND the up-input batch flush share the cadence, but carry DIFFERENT amounts (state: this snapshot;
    // input: all ticks since the last boundary). Boundaries land on tick 0, Divisor, 2*Divisor, ...
    public bool Advance() {
        bool sendBoundary = LocalTick % Divisor == 0;
        LocalTick++;
        return sendBoundary;
    }

    public void Reset() => LocalTick = 0;
}

// The UP path — per-tick input accumulated, flushed in a batch on the send boundary (the model P5a's
// input buffer + P5b's last-processed-seq build on). RecordInput is called EVERY tick (never gated by
// the divisor — the load-bearing rule); FlushBatch sends all buffered input in one packet, dropping none.
public sealed class InputUpStream {
    readonly List<uint> pending = new();   // input sequence numbers buffered since the last flush

    public int TotalRecorded { get; private set; }
    public int TotalSent { get; private set; }
    public int PacketsSent { get; private set; }
    public int PendingCount => pending.Count;

    // EVERY sim tick (the per-tick contract). seq is the monotonic LocalTick — the replay index P5b uses.
    public void RecordInput(uint seq) {
        pending.Add(seq);
        TotalRecorded++;
    }

    // On the send boundary — flush the whole batch in one packet, nothing dropped. Returns the batched
    // sequences (P3 packs these into the wire payload; P2 just proves the asymmetry holds).
    public uint[] FlushBatch() {
        if (pending.Count == 0)
            return Array.Empty<uint>();
        uint[] batch = pending.ToArray();
        pending.Clear();
        TotalSent += batch.Length;
        PacketsSent++;
        return batch;
    }

    public void Reset() {
        pending.Clear();
        TotalRecorded = TotalSent = PacketsSent = 0;
    }
}
