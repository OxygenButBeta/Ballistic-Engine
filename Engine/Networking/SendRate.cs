namespace BallisticEngine;

public sealed class SendRateClock {
    public const int DefaultDivisor = 3;

    public int Divisor { get; }
    public int LocalTick { get; private set; }

    public SendRateClock(int divisor = DefaultDivisor) {
        if (divisor < 1) throw new ArgumentOutOfRangeException(nameof(divisor), "send-rate divisor must be >= 1");
        Divisor = divisor;
    }

    public bool IsBoundary => LocalTick % Divisor == 0;

    public bool Advance() {
        bool sendBoundary = LocalTick % Divisor == 0;
        LocalTick++;
        return sendBoundary;
    }

    public void Reset() => LocalTick = 0;
}

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
