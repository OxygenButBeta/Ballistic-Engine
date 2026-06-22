using System.Diagnostics;

namespace BallisticEngine.Editor;

internal sealed class EditorProfilerBackend : IProfilerBackend {
    public const int HistorySize = 240;

    public struct ZoneRecord {
        public string Name;
        public int Depth;
        public double OffsetMs;
        public double DurationMs;
    }

    public sealed class Frame {
        public double TotalMs;
        public ZoneRecord[] Zones = new ZoneRecord[64];
        public int ZoneCount;
    }

    public bool Paused;

    readonly IProfilerBackend chained;
    readonly int mainThreadId = Environment.CurrentManagedThreadId;
    readonly Stopwatch clock = Stopwatch.StartNew();

    readonly Frame[] history = new Frame[HistorySize];
    int head;
    long completed;

    readonly int[] openZones = new int[64];
    int openCount;
    int skipped;
    double frameStartMs;

    public EditorProfilerBackend(IProfilerBackend chained) {
        this.chained = chained;
        for (var i = 0; i < HistorySize; i++)
            history[i] = new Frame();
    }

    public Frame CompletedFrame(int age) {
        var index = (head - 1 - age) % HistorySize;
        if (index < 0)
            index += HistorySize;
        return history[index];
    }

    public int AvailableFrames => (int)Math.Min(completed, HistorySize - 1);

    public ulong ZoneBegin(string name, uint color, uint line, string file, string member) {
        var handle = chained?.ZoneBegin(name, color, line, file, member) ?? 0;
        if (Environment.CurrentManagedThreadId != mainThreadId)
            return handle;

        if (openCount == openZones.Length) {
            skipped++;
            return handle;
        }

        Frame frame = history[head];
        if (frame.ZoneCount == frame.Zones.Length)
            Array.Resize(ref frame.Zones, frame.Zones.Length * 2);
        frame.Zones[frame.ZoneCount] = new ZoneRecord {
            Name = name ?? member ?? "zone",
            Depth = openCount,
            OffsetMs = clock.Elapsed.TotalMilliseconds - frameStartMs,
            DurationMs = -1,
        };
        openZones[openCount++] = frame.ZoneCount++;
        return handle;
    }

    public void ZoneEnd(ulong handle) {
        chained?.ZoneEnd(handle);
        if (Environment.CurrentManagedThreadId != mainThreadId)
            return;
        if (skipped > 0) {
            skipped--;
            return;
        }
        if (openCount == 0)
            return;

        Frame frame = history[head];
        ref ZoneRecord zone = ref frame.Zones[openZones[--openCount]];
        zone.DurationMs = clock.Elapsed.TotalMilliseconds - frameStartMs - zone.OffsetMs;
    }

    public void FrameMark() {
        chained?.FrameMark();
        if (Environment.CurrentManagedThreadId != mainThreadId)
            return;

        var now = clock.Elapsed.TotalMilliseconds;
        Frame frame = history[head];
        frame.TotalMs = now - frameStartMs;

        while (openCount > 0) {
            ref ZoneRecord zone = ref frame.Zones[openZones[--openCount]];
            zone.DurationMs = now - frameStartMs - zone.OffsetMs;
        }
        skipped = 0;

        if (!Paused) {
            completed++;
            head = (head + 1) % HistorySize;
        }
        Frame next = history[head];
        next.ZoneCount = 0;
        next.TotalMs = 0;
        frameStartMs = now;
    }

    public void Plot(string name, double value) => chained?.Plot(name, value);
    public void Message(string text) => chained?.Message(text);
}
