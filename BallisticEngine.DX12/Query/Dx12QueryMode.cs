using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace BallisticEngine.DX12;

// Headless scene-query mode for the `bal query` CLI. The CLI spawns the player (BALLISTIC_BACKEND=dx12)
// with BALLISTIC_QUERY=<spec.json> (+ BALLISTIC_SCENE / BALLISTIC_SCREENSHOT_PAUSED=1); after the scene has
// rendered a frame (the AS-feeding RuntimeSet<IStaticMeshRenderer> is populated), the headless runtime calls
// Run() here, which runs the requested query against the live scene TLAS and writes the result JSON to
// BALLISTIC_QUERY_OUT, then the process exits. This keeps the `bal` CLI device-free (same subprocess pattern
// as `bal render`). Spec format (written by the CLI):
//   { "op":"occupancy|visibility|classify|nudge|rooms",
//     "points":[[x,y,z],...], "pairs":[[[ax,ay,az],[bx,by,bz]],...], "probeRadius":200 }
public static class Dx12QueryMode {
    // Returns true if query mode ran (the caller should then exit). False = no query requested.
    public static bool Run(DX12HDRenderer renderer) {
        string specPath = Environment.GetEnvironmentVariable("BALLISTIC_QUERY");
        if (string.IsNullOrWhiteSpace(specPath)) return false;
        string outPath = Environment.GetEnvironmentVariable("BALLISTIC_QUERY_OUT")
                         ?? System.IO.Path.ChangeExtension(specPath, ".out.json");

        string json;
        try {
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(specPath));
            json = RunQuery(renderer, doc.RootElement);
        } catch (Exception e) {
            json = JsonSerializer.Serialize(new { ok = false, error = e.Message });
            Console.Error.WriteLine($"[Query] error: {e.Message}");
        }
        System.IO.File.WriteAllText(outPath, json);
        Console.WriteLine($"[Query] wrote {outPath}");
        return true;
    }

    static string RunQuery(DX12HDRenderer renderer, JsonElement spec) {
        string op = spec.TryGetProperty("op", out JsonElement o) ? (o.GetString() ?? "") : "";
        float probeRadius = spec.TryGetProperty("probeRadius", out JsonElement pr) && pr.TryGetSingle(out float r) ? r : 200f;

        using GpuSceneQuery q = renderer.CreateSceneQuery();

        switch (op) {
            case "occupancy": {
                List<Vector3> pts = ReadPoints(spec);
                bool[] occ = q.OccupancyAt(pts, probeRadius);
                var results = new object[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                    results[i] = new { point = Xyz(pts[i]), occupied = occ[i] };
                return JsonSerializer.Serialize(new { ok = true, op, available = q.Available, count = pts.Count, results });
            }
            case "classify": {
                List<Vector3> pts = ReadPoints(spec);
                GpuSceneQuery.SpaceClass[] cls = q.ClassifySpace(pts, probeRadius);
                var results = new object[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                    results[i] = new { point = Xyz(pts[i]), space = cls[i].ToString().ToLowerInvariant() };
                return JsonSerializer.Serialize(new { ok = true, op, available = q.Available, count = pts.Count, results });
            }
            case "nudge": {
                List<Vector3> pts = ReadPoints(spec);
                bool[] occ = q.OccupancyAt(pts, probeRadius);
                Vector3[] nud = q.NudgeToFreeSpace(pts, probeRadius);
                var results = new object[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                    results[i] = new { point = Xyz(pts[i]), wasOccupied = occ[i], free = Xyz(nud[i]) };
                return JsonSerializer.Serialize(new { ok = true, op, available = q.Available, count = pts.Count, results });
            }
            case "rooms": {
                List<Vector3> pts = ReadPoints(spec);
                int[] rooms = q.VisibilityClusters(pts);
                int roomCount = 0;
                foreach (int l in rooms) roomCount = Math.Max(roomCount, l + 1);
                var results = new object[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                    results[i] = new { point = Xyz(pts[i]), room = rooms[i] };
                return JsonSerializer.Serialize(new { ok = true, op, available = q.Available, count = pts.Count, rooms = roomCount, results });
            }
            case "visibility": {
                List<(Vector3 a, Vector3 b)> pairs = ReadPairs(spec);
                bool[] vis = q.Visibility(pairs);
                var results = new object[pairs.Count];
                for (int i = 0; i < pairs.Count; i++)
                    results[i] = new { a = Xyz(pairs[i].a), b = Xyz(pairs[i].b), visible = vis[i] };
                return JsonSerializer.Serialize(new { ok = true, op, available = q.Available, count = pairs.Count, results });
            }
            default:
                return JsonSerializer.Serialize(new {
                    ok = false, error = $"unknown op '{op}' (expected occupancy/visibility/classify/nudge/rooms)",
                });
        }
    }

    static float[] Xyz(Vector3 v) => new[] { v.X, v.Y, v.Z };

    static List<Vector3> ReadPoints(JsonElement spec) {
        var pts = new List<Vector3>();
        if (spec.TryGetProperty("points", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in arr.EnumerateArray())
                pts.Add(ReadVec3(e));
        return pts;
    }

    static List<(Vector3, Vector3)> ReadPairs(JsonElement spec) {
        var pairs = new List<(Vector3, Vector3)>();
        if (spec.TryGetProperty("pairs", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in arr.EnumerateArray()) {
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2)
                    pairs.Add((ReadVec3(e[0]), ReadVec3(e[1])));
            }
        return pairs;
    }

    static Vector3 ReadVec3(JsonElement e) {
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 3)
            return new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        throw new FormatException("expected a [x,y,z] array");
    }
}
