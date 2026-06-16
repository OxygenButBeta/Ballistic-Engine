using BallisticEngine.Networking;

namespace BallisticEngine;

// The unacknowledged-input ring (plan §8.2 / §13 P5a — "buffered by input-sequence"). On the input
// authority, every fixed tick's NetworkInput is pushed here keyed by its Seq. P5a only fills + reads it
// back in order (the replay store P5b consumes). P5b drains it on a server-ack: discard inputs at/below
// last-processed-seq, then replay every remaining (unacknowledged) input after snapping to the server
// state. A ring so a long unacked run (high latency) stays bounded — the cap is the resimulation
// lookback (P5e). In P5a the cap is generous so nothing is ever dropped (the harness asserts Dropped==0).
//
// Proven in %TEMP%\bal-predict-test (the isolated mesh-SDF-discipline harness) before this shipped.
public sealed class InputBuffer {
    readonly NetworkInput[] ring;
    readonly int capacity;
    int head;      // index of the next write slot
    int count;     // live entries

    // 256 entries ≈ 4.3 s at 60 Hz — far past any realistic unacked window before P5e's lookback cap.
    public InputBuffer(int capacity = 256) {
        this.capacity = Math.Max(1, capacity);
        ring = new NetworkInput[this.capacity];
    }

    public int Count => count;
    public int Capacity => capacity;

    // Entries evicted before being acked — a P5e signal (the unacked window outran the lookback). Stays
    // 0 in P5a (no acks yet, generous cap); P5b/P5e watch it.
    public int Dropped { get; private set; }

    // Push this tick's input. When full, the OLDEST entry is overwritten (bounded lookback) and counted
    // as dropped.
    public void Push(in NetworkInput input) {
        if (count == capacity) {
            // Full: overwrite the oldest. head currently points at the oldest (== next write when full).
            ring[head] = input;
            head = (head + 1) % capacity;
            Dropped++;
            return;
        }
        ring[head] = input;
        head = (head + 1) % capacity;
        count++;
    }

    int Mod(int i) => ((i % capacity) + capacity) % capacity;

    // The oldest live entry's seq (the front of the unacked window), or null if empty.
    public uint? OldestSeq => count == 0 ? null : ring[Mod(head - count)].Seq;

    // The newest buffered input (the one applied this tick), or null if empty.
    public NetworkInput? Latest => count == 0 ? null : ring[Mod(head - 1)];

    // Enumerate buffered inputs in seq order (oldest → newest) — the replay order P5b applies after a
    // server snap. Allocation-free over the ring (no per-tick call in P5a; replay is on-ack only).
    public IEnumerable<NetworkInput> InOrder() {
        int start = Mod(head - count);
        for (int i = 0; i < count; i++)
            yield return ring[Mod(start + i)];
    }

    // Drop every buffered input whose Seq is <= ackedSeq (the server has processed them — P5b ack path).
    // P5a does not call this (no acks yet); declared so the buffer's full lifecycle is in one place and
    // P5b just calls it. Returns how many were trimmed.
    public int AckThrough(uint ackedSeq) {
        int trimmed = 0;
        while (count > 0) {
            int oldest = Mod(head - count);
            // unsigned-safe "seq <= ackedSeq" within the live window (no wraparound at 60 Hz uint range)
            if (ring[oldest].Seq > ackedSeq)
                break;
            count--;
            trimmed++;
        }
        return trimmed;
    }

    public void Clear() { head = 0; count = 0; Dropped = 0; }
}
