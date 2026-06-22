namespace BallisticEngine;

public sealed class PoseHistory {
    readonly double[] ticks;
    readonly Vector3[] poses;
    int count;
    int head;

    public PoseHistory(int capacity) {
        if (capacity < 2) capacity = 2;
        ticks = new double[capacity];
        poses = new Vector3[capacity];
    }

    public int Count => count;
    public int Capacity => ticks.Length;
    public double OldestTick => count > 0 ? ticks[head] : 0;
    public double NewestTick => count > 0 ? ticks[(head + count - 1) % ticks.Length] : 0;

    int IndexAt(int i) => (head + i) % ticks.Length;

    public void Record(double tick, Vector3 pos) {
        if (count > 0) {
            int newest = IndexAt(count - 1);
            if (ticks[newest] == tick) { poses[newest] = pos; return; }
        }
        if (count < ticks.Length) {
            int idx = IndexAt(count);
            ticks[idx] = tick; poses[idx] = pos; count++;
        }
        else {
            ticks[head] = tick; poses[head] = pos;
            head = (head + 1) % ticks.Length;
        }
    }

    public Vector3 SampleAt(double renderTick) {
        if (count == 0) return Vector3.Zero;
        if (renderTick <= ticks[head]) return poses[head];
        int last = IndexAt(count - 1);
        if (renderTick >= ticks[last]) return poses[last];
        for (int i = 0; i < count - 1; i++) {
            int a = IndexAt(i), b = IndexAt(i + 1);
            if (renderTick >= ticks[a] && renderTick <= ticks[b]) {
                double span = ticks[b] - ticks[a];
                float t = span > 0 ? (float)((renderTick - ticks[a]) / span) : 0f;
                return Vector3.Lerp(poses[a], poses[b], t);
            }
        }
        return poses[last];
    }

    public void Clear() { count = 0; head = 0; }
}

public readonly struct LagRaycastHit {
    public readonly NetworkObject Pawn;
    public readonly float Distance;
    public readonly Vector3 Point;
    public LagRaycastHit(NetworkObject pawn, float distance, Vector3 point) {
        Pawn = pawn; Distance = distance; Point = point;
    }
}

public static class LagHitbox {
    public static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t) {
        t = 0f;
        Vector3 oc = origin - center;
        float b = Vector3.Dot(oc, dir);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;
        if (disc < 0f) return false;
        float sq = MathF.Sqrt(disc);
        float t0 = -b - sq;
        if (t0 >= 0f) { t = t0; return true; }
        float t1 = -b + sq;
        if (t1 >= 0f) { t = t1; return true; }

        return false;
    }
}
