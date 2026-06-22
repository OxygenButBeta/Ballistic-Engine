namespace BallisticEngine;

public sealed class InputUpStream {
    readonly List<uint> pending = new();

    public int TotalRecorded { get; private set; }
    public int TotalSent { get; private set; }
    public int PacketsSent { get; private set; }
    public int PendingCount => pending.Count;

    public void RecordInput(uint seq) {
        pending.Add(seq);
        TotalRecorded++;
    }

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
