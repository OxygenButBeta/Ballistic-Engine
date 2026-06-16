using System.Numerics;

namespace BallisticEngine;

// Proxy interpolation — the SimulatedProxy render path (plan §13 P5c, Valve cl_interp). A watching
// client receives a remote pawn's pose as snapshots at the DOWN send-rate (~20 Hz), arriving irregularly
// under loss/jitter. Snapping to the latest is jerky; extrapolating overshoots on a direction change.
// Instead the proxy renders ~InterpDelay ticks in the PAST and LERPS between the two snapshots bracketing
// the render time — smooth, slightly late, accurate motion. Proven in %TEMP%\bal-interp-test (10/10:
// tracks the true motion offset by the delay, smooth under 25% loss + jitter, holds on starvation
// without overshoot, follows a 90° turn without corner overshoot, deterministic).
//
// One of these lives per SimulatedProxy NetworkObject (the PredictTick branch that does NOT tick — a
// proxy has neither authority, so it is interpolated, never simulated locally). The TIME AXIS is the
// proxy's own monotonic tick counter (advanced once per fixed step): each received snapshot is stamped
// with the current tick on arrival, and Sample renders InterpDelay ticks back. Deterministic (no
// wall-clock), matching the engine's fixed-tick discipline.
public sealed class SnapshotInterpolator {
    readonly struct Sample {
        public readonly double Tick;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public Sample(double tick, Vector3 pos, Quaternion rot) { Tick = tick; Position = pos; Rotation = rot; }
    }

    // Snapshots in Tick order (jitter/reorder can deliver out of order — kept sorted so bracketing holds).
    readonly List<Sample> buffer = new();
    readonly double interpDelayTicks;

    // The default ~200ms back at 60 Hz — sized to cover a couple of consecutive dropped snapshots at the
    // 20 Hz send-rate (the cl_interp knob; a lossier link wants a larger buffer — see the harness note).
    public const double DefaultInterpDelayTicks = 12;

    public SnapshotInterpolator(double interpDelayTicks = DefaultInterpDelayTicks) =>
        this.interpDelayTicks = interpDelayTicks;

    public int BufferCount => buffer.Count;
    public bool Held { get; private set; }   // last Sample clamped to the newest (buffer starved)

    // A pose snapshot arrived; stamp it with the proxy's current tick and insert in Tick order. Duplicate
    // ticks replace (a re-sent snapshot).
    public void Receive(double tick, Vector3 position, Quaternion rotation) {
        var s = new Sample(tick, position, rotation);
        int i = buffer.Count;
        while (i > 0 && buffer[i - 1].Tick > tick) i--;
        if (i > 0 && buffer[i - 1].Tick == tick) { buffer[i - 1] = s; return; }
        buffer.Insert(i, s);
    }

    // Sample the interpolated pose at clockTick. renderTick = clockTick - InterpDelay (the past). Before
    // the oldest -> hold the oldest; after the newest -> the buffer starved, HOLD the newest (the
    // conservative cl_interp choice — extrapolation overshoots; a brief hold + catch-up is smoother).
    public bool TrySample(double clockTick, out Vector3 position, out Quaternion rotation) {
        Held = false;
        position = Vector3.Zero; rotation = Quaternion.Identity;
        if (buffer.Count == 0)
            return false;

        double renderTick = clockTick - interpDelayTicks;

        if (renderTick <= buffer[0].Tick) {
            position = buffer[0].Position; rotation = buffer[0].Rotation;
            return true;
        }
        if (renderTick >= buffer[^1].Tick) {
            Held = true;
            position = buffer[^1].Position; rotation = buffer[^1].Rotation;
            return true;
        }
        for (int i = 0; i < buffer.Count - 1; i++) {
            Sample a = buffer[i], b = buffer[i + 1];
            if (renderTick >= a.Tick && renderTick < b.Tick) {
                double span = b.Tick - a.Tick;
                float t = span > 0 ? (float)((renderTick - a.Tick) / span) : 0f;
                position = Vector3.Lerp(a.Position, b.Position, t);
                rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                return true;
            }
        }
        position = buffer[^1].Position; rotation = buffer[^1].Rotation;
        return true;
    }

    // Trim snapshots older than the render window (keep one before renderTick for the lower bracket).
    public void Trim(double clockTick) {
        double keepBefore = clockTick - interpDelayTicks - 1;
        while (buffer.Count > 2 && buffer[1].Tick < keepBefore)
            buffer.RemoveAt(0);
    }

    public void Clear() => buffer.Clear();
}
