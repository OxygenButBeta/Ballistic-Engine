using System.Diagnostics;

namespace BallisticEngine.Editor;

// Editor-side IProfilerBackend: records every main-thread zone into a fixed ring of frames
// for the Profiler panel, then forwards everything to an inner backend (Tracy) when one was
// installed first. Recording is a few array writes per zone, so it stays on for the whole
// session; Pause freezes the visible history without stopping the forwarding.
internal sealed class EditorProfilerBackend : IProfilerBackend {
    public const int HistorySize = 240;

    public struct ZoneRecord {
        public string Name;
        public int Depth;
        public double OffsetMs;    // from frame start
        public double DurationMs;
    }

    public sealed class Frame {
        public double TotalMs;
        public ZoneRecord[] Zones = new ZoneRecord[64];
        public int ZoneCount;
    }

    // While true the ring stops advancing (the panel shows a frozen history); the scratch
    // frame keeps being rewritten so resuming is seamless.
    public bool Paused;

    readonly IProfilerBackend chained;
    readonly int mainThreadId = Environment.CurrentManagedThreadId;
    readonly Stopwatch clock = Stopwatch.StartNew();

    readonly Frame[] history = new Frame[HistorySize];
    int head;          // the frame currently being recorded (scratch); completed frames sit behind it
    long completed;

    readonly int[] openZones = new int[64];   // indices into the scratch frame's zone array
    int openCount;
    int skipped;       // begins ignored because the stack was full — keeps end/begin balanced
    double frameStartMs;

    public EditorProfilerBackend(IProfilerBackend chained) {
        this.chained = chained;
        for (var i = 0; i < HistorySize; i++)
            history[i] = new Frame();
    }

    // Completed frames, age 0 = most recent. Valid ages: [0, AvailableFrames).
    public Frame CompletedFrame(int age) {
        var index = (head - 1 - age) % HistorySize;
        if (index < 0)
            index += HistorySize;
        return history[index];
    }

    // The head slot is scratch, so at most HistorySize-1 completed frames are retrievable.
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

        // Force-close anything still open so a completed frame never shows a -1 duration.
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
