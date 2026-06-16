using System.Numerics;

namespace BallisticEngine;

// Server-side lag compensation (plan §9 item 9 / §13 P8a — collider rollback / favor-the-shooter). A
// client renders other pawns ~InterpDelay ticks in the PAST (P5c) and sees them after a network delay,
// so a hitscan shot at a target that LOOKS on the crosshair would naively MISS against the CURRENT server
// pose — the target has already moved by the time the shot RPC arrives. The fix (Source-engine model):
// the server keeps a RING of each tracked pawn's historical poses, and a shot carries the client's
// RENDER-TICK (its interp clock − InterpDelay, what its screen actually showed). On the shot the server
// REWINDS every OTHER pawn's hitbox to the pose it interpolates at that render-tick, runs the ray, then
// RESTORES the live poses. The shooter is favored — the world is tested as it saw it.
//
// The rewind/restore/clamp ALGORITHM was proven in %TEMP%\bal-lagcomp-test (24/24: favor-the-shooter,
// restore-no-mutation, shooter-never-rewound, clamp/anti-abuse, nearest-of-many, no-false-positive,
// determinism) BEFORE this integration — the mesh-SDF discipline. The hitbox is a sphere so the rewind is
// exact + HEADLESS-testable (a dedicated hitscan test, decoupled from the Bepu world which only syncs at
// fixed-step boundaries and needs GL); a capsule/box refinement is a later extension.

// A bounded ring of (tick -> position) for ONE tracked pawn's hitbox. SampleAt interpolates between the
// two recorded ticks bracketing a (possibly fractional) render-tick, clamping out-of-range to the ends.
// Reflection-free, allocation-free on the steady path (records overwrite within the ring's capacity).
public sealed class PoseHistory {
    readonly double[] ticks;
    readonly Vector3[] poses;
    int count;     // number of valid entries (<= capacity)
    int head;      // index of the OLDEST entry; entries run head..head+count-1 (mod capacity)

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

    // Record this tick's pose. A re-record of the newest tick overwrites it (idempotent on the same tick);
    // a new tick appends, dropping the oldest once the ring is full. Ticks are assumed monotonic (the
    // network tick advances them) — out-of-order records are not expected on the authoritative server.
    public void Record(double tick, Vector3 pos) {
        if (count > 0) {
            int newest = IndexAt(count - 1);
            if (ticks[newest] == tick) { poses[newest] = pos; return; }   // overwrite same-tick re-record
        }
        if (count < ticks.Length) {
            int idx = IndexAt(count);
            ticks[idx] = tick; poses[idx] = pos; count++;
        }
        else {
            // full ring: overwrite the oldest, advance head (it becomes the new newest slot's predecessor).
            ticks[head] = tick; poses[head] = pos;
            head = (head + 1) % ticks.Length;
        }
    }

    // The interpolated pose at renderTick: older than the ring -> the oldest pose; newer -> the newest;
    // otherwise lerp between the bracketing pair. Clamping out-of-range is the conservative choice (a too-old
    // claimed tick is also capped by the server's max-rewind, ClampRenderTick).
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

// What a lag-compensated raycast hit — the rewound pawn + the impact. NetworkObject (not a raw netId, §3)
// so the caller acts on the object directly (apply damage, attribute the kill).
public readonly struct LagRaycastHit {
    public readonly NetworkObject Pawn;
    public readonly float Distance;
    public readonly Vector3 Point;
    public LagRaycastHit(NetworkObject pawn, float distance, Vector3 point) {
        Pawn = pawn; Distance = distance; Point = point;
    }
}

// The static ray-vs-hitbox test shared by the isolated harness's algorithm and the engine path. A sphere
// hitbox; the ray is (origin + t*dir, t>=0), dir unit. Returns the nearest forward t. Kept here so the
// rewind subsystem and any future shape extension use one tested primitive.
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
        if (t1 >= 0f) { t = t1; return true; }   // origin inside the sphere -> the exit point
        return false;                            // both roots behind the origin -> no forward hit
    }
}
