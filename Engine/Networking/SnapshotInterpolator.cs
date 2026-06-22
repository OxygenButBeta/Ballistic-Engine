namespace BallisticEngine;

public sealed class SnapshotInterpolator {
    readonly struct Sample {
        public readonly double Tick;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public Sample(double tick, Vector3 pos, Quaternion rot) { Tick = tick; Position = pos; Rotation = rot; }
    }

    readonly List<Sample> buffer = new();
    readonly double interpDelayTicks;

    public const double DefaultInterpDelayTicks = 12;

    public SnapshotInterpolator(double interpDelayTicks = DefaultInterpDelayTicks) =>
        this.interpDelayTicks = interpDelayTicks;

    public int BufferCount => buffer.Count;
    public bool Held { get; private set; }

    public void Receive(double tick, Vector3 position, Quaternion rotation) {
        var s = new Sample(tick, position, rotation);
        int i = buffer.Count;
        while (i > 0 && buffer[i - 1].Tick > tick) i--;
        if (i > 0 && buffer[i - 1].Tick == tick) { buffer[i - 1] = s; return; }
        buffer.Insert(i, s);
    }

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

    public void Trim(double clockTick) {
        double keepBefore = clockTick - interpDelayTicks - 1;
        while (buffer.Count > 2 && buffer[1].Tick < keepBefore)
            buffer.RemoveAt(0);
    }

    public void Clear() => buffer.Clear();
}
