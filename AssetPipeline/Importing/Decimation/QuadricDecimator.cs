using System;
using System.Collections.Generic;
using System.Numerics;

namespace BallisticEngine.AssetPipeline.Importing.Decimation;

// Deterministic quadric error-metric (Garland-Heckbert 1997) mesh decimator.
//
// DESIGN — index-only remap, vertices PRESERVED. The render side (GPU-driven ExecuteIndirect) binds ONE shared
// vertex buffer and selects a LOD by StartIndexLocation alone, with BaseVertexLocation hard-zero. So a LOD must
// reuse the ORIGINAL vertex array: this decimator never moves a vertex and never adds one — it only collapses an
// edge ONTO an existing endpoint and emits a smaller index buffer that references the same vertices. The unused
// vertices simply stay in the shared buffer (a few KB; the win is fewer TRIANGLES rasterised + vertex-shaded).
// This also means normals/UVs/tangents need NO recompute (we keep the surviving vertices' own attributes), and
// the result is exactly index-buffer-portable across LODs.
//
// DETERMINISM (a .bmesh artifact must be byte-identical run to run): single-threaded; the collapse priority queue
// breaks equal-error ties by (min,max) endpoint index; quadric sums accumulate in ascending index order; the
// collapse target is the lower-error endpoint with an index tie-break — no float-order ambiguity, no RNG.
//
// SEAM / BOUNDARY LOCK: an edge is collapsible only when BOTH endpoints are interior (not on a mesh boundary,
// UV seam, or attribute discontinuity). Boundary vertices are pinned, so submesh edges, open borders, and UV
// islands never tear — critical because LOD generation runs PER SUBMESH (the caller decimates one index range at
// a time, keeping the submesh partition intact).
public static class QuadricDecimator {
    // A symmetric 4x4 quadric stored as its 10 unique upper-triangular coefficients (the Q matrix of a vertex =
    // the sum of the fundamental error quadrics of its incident planes). vᵀQv is the squared distance to those
    // planes; the edge-collapse cost is that error evaluated at the surviving endpoint.
    struct Quadric {
        public double A00, A01, A02, A03, A11, A12, A13, A22, A23, A33;
        public void AddPlane(double a, double b, double c, double d) {
            A00 += a * a; A01 += a * b; A02 += a * c; A03 += a * d;
            A11 += b * b; A12 += b * c; A13 += b * d;
            A22 += c * c; A23 += c * d; A33 += d * d;
        }
        public void Add(in Quadric q) {
            A00 += q.A00; A01 += q.A01; A02 += q.A02; A03 += q.A03;
            A11 += q.A11; A12 += q.A12; A13 += q.A13; A22 += q.A22; A23 += q.A23; A33 += q.A33;
        }
        // vᵀQv for the homogeneous point (x,y,z,1).
        public double Error(Vector3 v) {
            double x = v.X, y = v.Y, z = v.Z;
            return A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + 2 * A03 * x
                 + A11 * y * y + 2 * A12 * y * z + 2 * A13 * y
                 + A22 * z * z + 2 * A23 * z
                 + A33;
        }
    }

    readonly struct Edge : IEquatable<Edge> {
        public readonly int A, B;   // A < B (canonical)
        public Edge(int a, int b) { if (a < b) { A = a; B = b; } else { A = b; B = a; } }
        public bool Equals(Edge o) => A == o.A && B == o.B;
        public override bool Equals(object o) => o is Edge e && Equals(e);
        public override int GetHashCode() => A * 73856093 ^ B * 19349663;
    }

    // A pending collapse: error + the edge + the endpoint we collapse ONTO (kept), the other is removed.
    readonly struct Candidate : IComparable<Candidate> {
        public readonly double Cost; public readonly int Keep, Remove, Version;
        public Candidate(double cost, int keep, int remove, int version) {
            Cost = cost; Keep = keep; Remove = remove; Version = version;
        }
        // Deterministic ordering: lower cost first, then (min,max) endpoint for a stable tie-break.
        public int CompareTo(Candidate o) {
            int c = Cost.CompareTo(o.Cost); if (c != 0) return c;
            int lo = Math.Min(Keep, Remove), olo = Math.Min(o.Keep, o.Remove);
            c = lo.CompareTo(olo); if (c != 0) return c;
            return Math.Max(Keep, Remove).CompareTo(Math.Max(o.Keep, o.Remove));
        }
    }

    // Simplify ONE index range (a submesh) down to ~targetRatio of its triangles. `positions` is the FULL shared
    // vertex array (not sliced — indices are absolute); `indices` is this submesh's index range copied out.
    // Returns a NEW, smaller index array referencing the SAME positions. targetRatio in (0,1]; >=1 returns a copy.
    public static uint[] Simplify(Vector3[] positions, uint[] indices, float targetRatio) {
        int triCount = indices.Length / 3;
        if (targetRatio >= 1f || triCount <= 2) return (uint[])indices.Clone();
        int targetTris = Math.Max(2, (int)MathF.Round(triCount * targetRatio));
        if (targetTris >= triCount) return (uint[])indices.Clone();

        // --- Build the working triangle list (local, mutable) + the set of vertices this submesh touches. ---
        var tris = new List<(int a, int b, int c)>(triCount);
        for (int i = 0; i < indices.Length; i += 3)
            tris.Add(((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]));

        // Per-vertex quadric (only for touched vertices) + incident-triangle adjacency.
        var quadric = new Dictionary<int, Quadric>();
        var incident = new Dictionary<int, List<int>>();   // vertex → triangle indices in `tris`
        void Touch(int v, int t) {
            if (!incident.TryGetValue(v, out var list)) { list = new List<int>(); incident[v] = list; }
            list.Add(t);
            if (!quadric.ContainsKey(v)) quadric[v] = default;
        }
        for (int t = 0; t < tris.Count; t++) { Touch(tris[t].a, t); Touch(tris[t].b, t); Touch(tris[t].c, t); }

        // Accumulate the plane quadric of every triangle onto its three vertices (ascending vertex order so the
        // double sums are order-deterministic).
        var verts = new List<int>(quadric.Keys); verts.Sort();
        foreach (int t in StableTriOrder(tris.Count)) {
            var (a, b, c) = tris[t];
            Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
            Vector3 n = Vector3.Cross(pb - pa, pc - pa);
            float len = n.Length();
            if (len < 1e-12f) continue;   // degenerate
            n /= len;
            double d = -Vector3.Dot(n, pa);
            var q = default(Quadric); q.AddPlane(n.X, n.Y, n.Z, d);
            // add to each endpoint (the Dictionary value is a struct → read-modify-write)
            AddQ(quadric, a, q); AddQ(quadric, b, q); AddQ(quadric, c, q);
        }

        // --- Boundary / seam lock: a vertex is pinned if it sits on an open edge (an edge used by exactly one
        // triangle within this submesh). Collapsing onto/away from a pinned vertex along a boundary tears the
        // shell, so we forbid collapsing any edge with a pinned endpoint (conservative but crack-free). ---
        var edgeUse = new Dictionary<Edge, int>();
        foreach (var (a, b, c) in tris) { Bump(edgeUse, a, b); Bump(edgeUse, b, c); Bump(edgeUse, c, a); }
        var pinned = new HashSet<int>();
        foreach (var kv in edgeUse) if (kv.Value == 1) { pinned.Add(kv.Key.A); pinned.Add(kv.Key.B); }

        // --- Candidate priority queue over collapsible interior edges. ---
        var alive = new bool[tris.Count]; for (int i = 0; i < alive.Length; i++) alive[i] = true;
        var removed = new HashSet<int>();                 // removed vertices (collapsed away)
        var version = new Dictionary<int, int>();         // bump on any change touching a vertex → stale-candidate guard
        foreach (int v in verts) version[v] = 0;

        var pq = new SortedSet<CandKey>();                // ordered candidate set (acts as a deterministic heap)
        void PushEdge(int u, int w) {
            if (u == w || pinned.Contains(u) || pinned.Contains(w)) return;
            if (removed.Contains(u) || removed.Contains(w)) return;
            // Evaluate collapsing onto u and onto w; pick the cheaper (the kept endpoint's quadric = qu+qw).
            Quadric qq = quadric[u]; qq.Add(quadric[w]);
            double eu = qq.Error(positions[u]);
            double ew = qq.Error(positions[w]);
            int keep, rem; double cost;
            if (eu < ew || (eu == ew && u < w)) { keep = u; rem = w; cost = eu; }
            else { keep = w; rem = u; cost = ew; }
            pq.Add(new CandKey(new Candidate(cost, keep, rem, version[keep] + version[rem])));
        }
        foreach (var kv in edgeUse) PushEdge(kv.Key.A, kv.Key.B);

        int liveTris = tris.Count;
        while (liveTris > targetTris && pq.Count > 0) {
            CandKey top = pq.Min; pq.Remove(top);
            Candidate cand = top.C;
            int keep = cand.Keep, rem = cand.Remove;
            if (removed.Contains(keep) || removed.Contains(rem)) continue;
            if (version[keep] + version[rem] != cand.Version) continue;   // stale (endpoints changed) → skip

            // Collapse `rem` onto `keep`: rewrite triangles, drop those that degenerate, merge quadrics/adjacency.
            Quadric merged = quadric[keep]; merged.Add(quadric[rem]); quadric[keep] = merged;
            if (!incident.TryGetValue(rem, out var remTris)) remTris = new List<int>();
            var affectedNeighbours = new HashSet<int>();
            foreach (int t in remTris) {
                if (!alive[t]) continue;
                var (a, b, c) = tris[t];
                int na = a == rem ? keep : a, nb = b == rem ? keep : b, nc = c == rem ? keep : c;
                if (na == nb || nb == nc || nc == na) {           // collapsed to a sliver → kill the triangle
                    alive[t] = false; liveTris--;
                    foreach (int v in new[] { a, b, c }) if (v != rem) affectedNeighbours.Add(v);
                    continue;
                }
                tris[t] = (na, nb, nc);
                incident[keep].Add(t);
                affectedNeighbours.Add(na); affectedNeighbours.Add(nb); affectedNeighbours.Add(nc);
            }
            removed.Add(rem);
            version[keep] = version[keep] + 1;
            foreach (int v in affectedNeighbours) if (version.ContainsKey(v)) version[v]++;
            // Re-push edges from `keep` to its (still-alive) neighbours.
            foreach (int t in incident[keep]) {
                if (!alive[t]) continue;
                var (a, b, c) = tris[t];
                if (a == keep) { PushEdge(keep, b); PushEdge(keep, c); }
                else if (b == keep) { PushEdge(keep, a); PushEdge(keep, c); }
                else if (c == keep) { PushEdge(keep, a); PushEdge(keep, b); }
            }
        }

        // --- Emit the surviving triangles as a fresh index buffer (same vertex references). ---
        var outIdx = new List<uint>(liveTris * 3);
        for (int t = 0; t < tris.Count; t++) {
            if (!alive[t]) continue;
            outIdx.Add((uint)tris[t].a); outIdx.Add((uint)tris[t].b); outIdx.Add((uint)tris[t].c);
        }
        return outIdx.ToArray();
    }

    static IEnumerable<int> StableTriOrder(int n) { for (int i = 0; i < n; i++) yield return i; }
    static void AddQ(Dictionary<int, Quadric> map, int v, in Quadric q) { var cur = map[v]; cur.Add(q); map[v] = cur; }
    static void Bump(Dictionary<Edge, int> map, int a, int b) { var e = new Edge(a, b); map[e] = map.TryGetValue(e, out int n) ? n + 1 : 1; }

    // SortedSet needs a fully-ordered, unique key. Wrap Candidate with a strict comparator (CompareTo never 0 for
    // distinct edges) so two candidates with identical cost+edge but different versions don't collide.
    readonly struct CandKey : IComparable<CandKey> {
        public readonly Candidate C;
        public CandKey(Candidate c) { C = c; }
        public int CompareTo(CandKey o) {
            int c = C.CompareTo(o.C); if (c != 0) return c;
            return C.Version.CompareTo(o.C.Version);
        }
    }
}
