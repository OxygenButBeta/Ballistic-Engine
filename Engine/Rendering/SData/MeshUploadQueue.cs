using System.Diagnostics;

namespace BallisticEngine;

public static class MeshUploadQueue
{
    static readonly List<Mesh> pending = new();
    static readonly object gate = new();

    static readonly double BudgetMs = ResolveBudget();

    static double ResolveBudget() =>
        double.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_MESH_STREAM_MS"),
            System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms > 0 ? ms : 2.5;

    public static bool HasPending { get { lock (gate) return pending.Count > 0; } }
    public static int PendingCount { get { lock (gate) return pending.Count; } }

    public static void Enqueue(Mesh mesh)
    {
        if (mesh is null) return;
        lock (gate) {
            if (!pending.Contains(mesh))
                pending.Add(mesh);
        }
    }

    public static int PumpUploads(Func<Mesh, System.Numerics.Vector3?> worldPosOf, System.Numerics.Vector3 cameraPos)
    {
        int uploaded = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < BudgetMs) {
            Mesh next;
            lock (gate) {
                if (pending.Count == 0) break;
                next = TakeClosest(worldPosOf, cameraPos);
            }
            next.EnsureUploaded();
            uploaded++;
        }
        return uploaded;
    }

    static Mesh TakeClosest(Func<Mesh, System.Numerics.Vector3?> worldPosOf, System.Numerics.Vector3 cameraPos)
    {
        int best = 0;
        if (worldPosOf is not null) {
            float bestDist = float.MaxValue;
            for (int i = 0; i < pending.Count; i++) {
                System.Numerics.Vector3? p = worldPosOf(pending[i]);
                float d = p is { } pos ? System.Numerics.Vector3.DistanceSquared(pos, cameraPos) : float.MaxValue;
                if (d < bestDist) { bestDist = d; best = i; }
            }
        }
        Mesh m = pending[best];
        pending.RemoveAt(best);
        return m;
    }

    public static void Clear()
    {
        lock (gate) pending.Clear();
    }
}
