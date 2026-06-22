namespace BallisticEngine.AssetPipeline.Importing.Decimation;

public static class QuadricDecimator {
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

        public double Error(Vector3 v) {
            double x = v.X, y = v.Y, z = v.Z;
            return A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + 2 * A03 * x
                 + A11 * y * y + 2 * A12 * y * z + 2 * A13 * y
                 + A22 * z * z + 2 * A23 * z
                 + A33;
        }
    }

    readonly struct Edge : IEquatable<Edge> {
        public readonly int A, B;
        public Edge(int a, int b) { if (a < b) { A = a; B = b; } else { A = b; B = a; } }
        public bool Equals(Edge o) => A == o.A && B == o.B;
        public override bool Equals(object o) => o is Edge e && Equals(e);
        public override int GetHashCode() => A * 73856093 ^ B * 19349663;
    }

    readonly struct Candidate : IComparable<Candidate> {
        public readonly double Cost; public readonly int Keep, Remove, Version;
        public Candidate(double cost, int keep, int remove, int version) {
            Cost = cost; Keep = keep; Remove = remove; Version = version;
        }

        public int CompareTo(Candidate o) {
            int c = Cost.CompareTo(o.Cost); if (c != 0) return c;
            int lo = Math.Min(Keep, Remove), olo = Math.Min(o.Keep, o.Remove);
            c = lo.CompareTo(olo); if (c != 0) return c;
            return Math.Max(Keep, Remove).CompareTo(Math.Max(o.Keep, o.Remove));
        }
    }

    public static uint[] Simplify(Vector3[] positions, uint[] indices, float targetRatio) {
        int triCount = indices.Length / 3;
        if (targetRatio >= 1f || triCount <= 2) return (uint[])indices.Clone();
        int targetTris = Math.Max(2, (int)MathF.Round(triCount * targetRatio));
        if (targetTris >= triCount) return (uint[])indices.Clone();

        var tris = new List<(int a, int b, int c)>(triCount);
        for (int i = 0; i < indices.Length; i += 3)
            tris.Add(((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]));

        var quadric = new Dictionary<int, Quadric>();
        var incident = new Dictionary<int, List<int>>();

        void Touch(int v, int t) {
            if (!incident.TryGetValue(v, out var list)) { list = new List<int>(); incident[v] = list; }
            list.Add(t);
            if (!quadric.ContainsKey(v)) quadric[v] = default;
        }
        for (int t = 0; t < tris.Count; t++) { Touch(tris[t].a, t); Touch(tris[t].b, t); Touch(tris[t].c, t); }

        var verts = new List<int>(quadric.Keys); verts.Sort();
        foreach (int t in StableTriOrder(tris.Count)) {
            var (a, b, c) = tris[t];
            Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
            Vector3 n = Vector3.Cross(pb - pa, pc - pa);
            float len = n.Length();
            if (len < 1e-12f) continue;
            n /= len;
            double d = -Vector3.Dot(n, pa);
            var q = default(Quadric); q.AddPlane(n.X, n.Y, n.Z, d);
            AddQ(quadric, a, q); AddQ(quadric, b, q); AddQ(quadric, c, q);
        }

        var edgeUse = new Dictionary<Edge, int>();
        foreach (var (a, b, c) in tris) { Bump(edgeUse, a, b); Bump(edgeUse, b, c); Bump(edgeUse, c, a); }
        var pinned = new HashSet<int>();
        foreach (var kv in edgeUse) if (kv.Value == 1) { pinned.Add(kv.Key.A); pinned.Add(kv.Key.B); }

        var alive = new bool[tris.Count]; for (int i = 0; i < alive.Length; i++) alive[i] = true;
        var removed = new HashSet<int>();
        var version = new Dictionary<int, int>();
        foreach (int v in verts) version[v] = 0;

        var pq = new SortedSet<CandKey>();

        void PushEdge(int u, int w) {
            if (u == w || pinned.Contains(u) || pinned.Contains(w)) return;
            if (removed.Contains(u) || removed.Contains(w)) return;
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
            if (version[keep] + version[rem] != cand.Version) continue;

            Quadric merged = quadric[keep]; merged.Add(quadric[rem]); quadric[keep] = merged;
            if (!incident.TryGetValue(rem, out var remTris)) remTris = new List<int>();
            var affectedNeighbours = new HashSet<int>();
            foreach (int t in remTris) {
                if (!alive[t]) continue;
                var (a, b, c) = tris[t];
                int na = a == rem ? keep : a, nb = b == rem ? keep : b, nc = c == rem ? keep : c;
                if (na == nb || nb == nc || nc == na) {
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
            foreach (int t in incident[keep]) {
                if (!alive[t]) continue;
                var (a, b, c) = tris[t];
                if (a == keep) { PushEdge(keep, b); PushEdge(keep, c); }
                else if (b == keep) { PushEdge(keep, a); PushEdge(keep, c); }
                else if (c == keep) { PushEdge(keep, a); PushEdge(keep, b); }
            }
        }

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

    readonly struct CandKey : IComparable<CandKey> {
        public readonly Candidate C;
        public CandKey(Candidate c) { C = c; }
        public int CompareTo(CandKey o) {
            int c = C.CompareTo(o.C); if (c != 0) return c;
            return C.Version.CompareTo(o.C.Version);
        }
    }
}
