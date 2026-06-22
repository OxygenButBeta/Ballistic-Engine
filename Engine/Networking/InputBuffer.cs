using BallisticEngine.Networking;

namespace BallisticEngine;

public sealed class InputBuffer {
    readonly NetworkInput[] ring;
    readonly int capacity;
    int head;
    int count;

    public const int DefaultLookbackCap = 32;

    public InputBuffer(int capacity = DefaultLookbackCap) {
        this.capacity = Math.Max(1, capacity);
        ring = new NetworkInput[this.capacity];
    }

    public int Count => count;
    public int Capacity => capacity;

    public int Dropped { get; private set; }

    public void Push(in NetworkInput input) {
        if (count == capacity) {
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

    public uint? OldestSeq => count == 0 ? null : ring[Mod(head - count)].Seq;

    public NetworkInput? Latest => count == 0 ? null : ring[Mod(head - 1)];

    public IEnumerable<NetworkInput> InOrder() {
        int start = Mod(head - count);
        for (int i = 0; i < count; i++)
            yield return ring[Mod(start + i)];
    }

    public int AckThrough(uint ackedSeq) {
        int trimmed = 0;
        while (count > 0) {
            int oldest = Mod(head - count);
            if (ring[oldest].Seq > ackedSeq)
                break;
            count--;
            trimmed++;
        }
        return trimmed;
    }

    public void Clear() { head = 0; count = 0; Dropped = 0; }
}
