using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// Non-blocking per-pass GPU timing. Each timed scope records a pair of GL_TIMESTAMP queries
// (timestamps, unlike GL_TIME_ELAPSED, may overlap freely so scopes can nest); whole frames
// are drained a few frames later once their last query has a result, so reading never syncs
// the pipeline. One instance per render target — the editor times its Scene and Game renders
// independently.
sealed class GLGpuTimers {
    struct Span {
        public string Name;
        public int Begin, End;
    }

    sealed class Frame {
        public readonly List<Span> Spans = new();
    }

    readonly Stack<int> queryPool = new();
    readonly Queue<Frame> inFlight = new();
    readonly Stack<Frame> framePool = new();
    Frame current;

    int Alloc() => queryPool.Count > 0 ? queryPool.Pop() : GL.GenQuery();

    public void BeginFrame() {
        current = framePool.Count > 0 ? framePool.Pop() : new Frame();
    }

    public PassScope Time(string name) {
        if (current is null)
            return default;
        int begin = Alloc(), end = Alloc();
        GL.QueryCounter(begin, QueryCounterTarget.Timestamp);
        current.Spans.Add(new Span { Name = name, Begin = begin, End = end });
        return new PassScope(end);
    }

    // Query id 0 is never produced by GenQuery, so default(PassScope) is a safe no-op.
    public readonly struct PassScope : IDisposable {
        readonly int end;
        internal PassScope(int end) => this.end = end;

        public void Dispose() {
            if (end != 0)
                GL.QueryCounter(end, QueryCounterTarget.Timestamp);
        }
    }

    // Queues the current frame and drains every COMPLETED in-flight frame into stats. A frame
    // is complete when its last timestamp has a result; earlier timestamps then must too.
    public void EndFrame(RenderStats stats) {
        if (current is not null) {
            inFlight.Enqueue(current);
            current = null;
        }

        while (inFlight.Count > 0) {
            Frame oldest = inFlight.Peek();
            if (oldest.Spans.Count == 0) {
                Recycle(inFlight.Dequeue());
                continue;
            }

            GL.GetQueryObject(oldest.Spans[^1].End, GetQueryObjectParam.QueryResultAvailable,
                out int available);
            if (available == 0)
                return; // oldest not ready -> newer frames aren't either

            inFlight.Dequeue();
            stats.GpuPasses.Clear();
            long frameBegin = long.MaxValue, frameEnd = 0;
            foreach (Span span in oldest.Spans) {
                GL.GetQueryObject(span.Begin, GetQueryObjectParam.QueryResult, out long t0);
                GL.GetQueryObject(span.End, GetQueryObjectParam.QueryResult, out long t1);
                stats.GpuPasses.Add((span.Name, (t1 - t0) / 1_000_000.0));
                frameBegin = Math.Min(frameBegin, t0);
                frameEnd = Math.Max(frameEnd, t1);
            }

            stats.GpuFrameMs = (frameEnd - frameBegin) / 1_000_000.0;
            Recycle(oldest);
        }
    }

    void Recycle(Frame frame) {
        foreach (Span span in frame.Spans) {
            queryPool.Push(span.Begin);
            queryPool.Push(span.End);
        }

        frame.Spans.Clear();
        framePool.Push(frame);
    }
}
