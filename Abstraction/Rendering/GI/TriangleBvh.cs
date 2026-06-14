using OpenTK.Mathematics;

namespace BallisticEngine.GI;

// A median-split AABB BVH over a triangle list, built once per mesh bake. Two queries:
//   • ClosestDistanceSq(p)  — squared distance to the nearest triangle, with branch-and-bound
//     pruning (descend the nearer child first, skip a node whose AABB is farther than the best).
//   • IsInside(p)           — ray-stab PARITY across 6 axis rays, majority vote. Winding-agnostic
//     (counts crossings, ignores triangle facing) so it survives merged/welded meshes where the
//     old face-normal sign produced the all-teal failure.
//
// CPU-only (BCL + OpenTK.Mathematics).
internal sealed class TriangleBvh {
    struct Node {
        public Vector3 Min, Max;
        public int Left;       // child index, or -1 for a leaf
        public int Start, Count; // leaf triangle range into `order`
    }

    readonly MeshSdfBaker.Triangle[] tris;
    readonly int[] order;       // triangle indices, partitioned by the build
    Node[] nodes;
    int nodeCount;

    const int LeafSize = 4;

    public TriangleBvh(MeshSdfBaker.Triangle[] triangles) {
        tris = triangles;
        order = new int[triangles.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        // Balanced trees need ~2*leaves nodes; uneven splits (duplicate centroids) can need more,
        // so the array grows on demand rather than risk an overflow.
        nodes = new Node[Math.Max(4, 2 * (triangles.Length / LeafSize + 1))];
        Build(0, triangles.Length);
    }

    int Build(int start, int count) {
        if (nodeCount >= nodes.Length)
            Array.Resize(ref nodes, nodes.Length * 2);
        int nodeIndex = nodeCount++;
        ref Node node = ref nodes[nodeIndex];

        // Compute the node AABB over its triangles.
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = start; i < start + count; i++) {
            MeshSdfBaker.Triangle t = tris[order[i]];
            min = Vector3.ComponentMin(min, t.Min);
            max = Vector3.ComponentMax(max, t.Max);
        }
        node.Min = min;
        node.Max = max;

        if (count <= LeafSize) {
            node.Left = -1;
            node.Start = start;
            node.Count = count;
            return nodeIndex;
        }

        // Split along the longest AABB axis at the centroid median.
        Vector3 ext = max - min;
        int axis = ext.X >= ext.Y ? (ext.X >= ext.Z ? 0 : 2) : (ext.Y >= ext.Z ? 1 : 2);

        int mid = start + count / 2;
        // nth_element-style partition around the median centroid on `axis`.
        NthElement(start, start + count - 1, mid, axis);

        // The recursive Build mutates nodeCount; capture children indices via return.
        // Left covers [start, mid); right covers [mid, start+count).
        int left = Build(start, mid - start);
        int right = Build(mid, start + count - mid);
        nodes[nodeIndex].Left = left;
        nodes[nodeIndex].Start = right; // stash right child index in Start for internal nodes
        nodes[nodeIndex].Count = -1;    // mark internal
        return nodeIndex;
    }

    static float AxisValue(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // Quickselect partition so that element at `nth` is the one that would be there if sorted by
    // centroid on `axis`, and everything left of it is <=, right is >=.
    void NthElement(int lo, int hi, int nth, int axis) {
        while (lo < hi) {
            float pivot = AxisValue(tris[order[(lo + hi) / 2]].Centroid, axis);
            int i = lo, j = hi;
            while (i <= j) {
                while (AxisValue(tris[order[i]].Centroid, axis) < pivot) i++;
                while (AxisValue(tris[order[j]].Centroid, axis) > pivot) j--;
                if (i <= j) {
                    (order[i], order[j]) = (order[j], order[i]);
                    i++; j--;
                }
            }
            if (nth <= j) hi = j;
            else if (nth >= i) lo = i;
            else break;
        }
    }

    bool IsLeaf(in Node n) => n.Count >= 0;

    // ---- Closest distance (squared) ---------------------------------------
    public float ClosestDistanceSq(Vector3 p) {
        float best = float.MaxValue;
        int dummy = -1;
        ClosestRecursive(0, p, ref best, ref dummy, false);
        return best;
    }

    // Variant that also reports the ORIGINAL-array index of the nearest triangle (for per-voxel
    // material lookup — the Lumen albedo clipmap reads the nearest surface's material colour). The
    // index is into the `tris` array as passed to the ctor (1:1 with the caller's per-triangle data).
    public float ClosestDistanceSq(Vector3 p, out int triIndex) {
        float best = float.MaxValue;
        triIndex = -1;
        ClosestRecursive(0, p, ref best, ref triIndex, true);
        return best;
    }

    void ClosestRecursive(int nodeIndex, Vector3 p, ref float best, ref int bestTri, bool trackTri) {
        ref Node node = ref nodes[nodeIndex];
        if (AabbDistanceSq(node.Min, node.Max, p) >= best)
            return;

        if (IsLeaf(node)) {
            for (int i = node.Start; i < node.Start + node.Count; i++) {
                int ti = order[i];
                MeshSdfBaker.Triangle t = tris[ti];
                float d = PointTriangleDistanceSq(p, t.A, t.B, t.C);
                if (d < best) { best = d; if (trackTri) bestTri = ti; }
            }
            return;
        }

        int left = node.Left;
        int right = node.Start; // internal nodes stash right index in Start
        float dl = AabbDistanceSq(nodes[left].Min, nodes[left].Max, p);
        float dr = AabbDistanceSq(nodes[right].Min, nodes[right].Max, p);
        // Descend the nearer child first so `best` tightens before pruning the far one.
        if (dl <= dr) {
            ClosestRecursive(left, p, ref best, ref bestTri, trackTri);
            ClosestRecursive(right, p, ref best, ref bestTri, trackTri);
        } else {
            ClosestRecursive(right, p, ref best, ref bestTri, trackTri);
            ClosestRecursive(left, p, ref best, ref bestTri, trackTri);
        }
    }

    // Closest SURFACE POINT to p (not just distance) + the nearest triangle index. The point is the
    // JFA seed coordinate the GPU jump-flood propagates (SdfSeedExtractor); the triangle index feeds
    // the per-voxel albedo. Returns dist^2. Implemented as a closest-tri descent then one point solve
    // on the winner (the per-leaf point solve in ClosestRecursive would cost a Vector3 write per tri).
    public float ClosestPoint(Vector3 p, out Vector3 point, out int triIndex) {
        float distSq = ClosestDistanceSq(p, out triIndex);
        if (triIndex >= 0) {
            MeshSdfBaker.Triangle t = tris[triIndex];
            point = ClosestPointOnTriangle(p, t.A, t.B, t.C);
        } else {
            point = p;
        }
        return distSq;
    }

    // Ericson closest-point-on-triangle (the point counterpart of PointTriangleDistanceSq). Same
    // barycentric region tests; returns the closest point itself.
    static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
            float v0 = d1 / (d1 - d3);
            return a + v0 * ab;
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
            float w0 = d2 / (d2 - d6);
            return a + w0 * ac;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) {
            float w1 = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return b + w1 * (c - b);
        }

        float denom = 1f / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return a + ab * vv + ac * ww;
    }

    static float AabbDistanceSq(Vector3 min, Vector3 max, Vector3 p) {
        float dx = MathF.Max(MathF.Max(min.X - p.X, p.X - max.X), 0f);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, p.Y - max.Y), 0f);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, p.Z - max.Z), 0f);
        return dx * dx + dy * dy + dz * dz;
    }

    // Ericson, Real-Time Collision Detection — closest point on triangle to p, return dist^2.
    static float PointTriangleDistanceSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return (p - a).LengthSquared;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return (p - b).LengthSquared;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
            float v0 = d1 / (d1 - d3);
            return (p - (a + v0 * ab)).LengthSquared;
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return (p - c).LengthSquared;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
            float w0 = d2 / (d2 - d6);
            return (p - (a + w0 * ac)).LengthSquared;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) {
            float w1 = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return (p - (b + w1 * (c - b))).LengthSquared;
        }

        float denom = 1f / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return (p - (a + ab * vv + ac * ww)).LengthSquared;
    }

    // ---- Inside test: parity majority over GENERIC (non-axis) rays --------
    // Axis-aligned rays on axis-aligned geometry are the worst case: they travel parallel to faces
    // and pass through the shared diagonal edge of split quads, so every such ray abstains and the
    // vote collapses (the salt-and-pepper interior bug). Generic irrational-slope directions never
    // lie in a face plane and almost never hit a shared edge, so each ray gives a clean parity.
    static readonly Vector3[] RayDirs = BuildRays();

    static Vector3[] BuildRays() {
        // 7 directions spread over the sphere with irrational components (no two parallel, none
        // axis-aligned). Odd count so a plain majority can never tie.
        var dirs = new Vector3[] {
            new( 0.5257f,  0.8507f,  0.0000f),
            new(-0.5257f,  0.8507f,  0.0000f),
            new( 0.8507f,  0.0000f,  0.5257f),
            new(-0.8507f,  0.0000f, -0.5257f),
            new( 0.0000f,  0.5257f, -0.8507f),
            new( 0.3333f, -0.6667f,  0.6667f),
            new(-0.6667f, -0.3333f,  0.6667f),
        };
        for (int i = 0; i < dirs.Length; i++) dirs[i] = Vector3.Normalize(dirs[i]);
        return dirs;
    }

    public bool IsInside(Vector3 p) => IsInside(p, RayDirs.Length);

    // maxRays caps the number of parity rays cast (clamped to the available directions). The full
    // 7-ray vote is robust on adversarial welded geometry; a COARSE warm-up bake can afford fewer
    // rays (3) — a temporary, soon-replaced field tolerates the occasional mis-signed voxel, and
    // fewer ray traversals is the dominant per-voxel saving when large coarse cells put most voxels
    // inside the sign-test band. Odd counts keep the majority untieable.
    public bool IsInside(Vector3 p, int maxRays) {
        int n = Math.Clamp(maxRays, 1, RayDirs.Length);
        int insideVotes = 0, voters = 0;
        for (int r = 0; r < n; r++) {
            int crossings = CountCrossings(p, RayDirs[r]);
            if (crossings < 0) continue;           // ray grazed an edge — abstain
            voters++;
            if ((crossings & 1) == 1) insideVotes++;
        }
        if (voters == 0) return false;             // all abstained (degenerate) — treat as outside
        return insideVotes * 2 > voters;           // majority of actual voters
    }

    // Counts triangle crossings along the ray from p. Returns -1 if any hit is too close to a
    // triangle edge/vertex to count reliably (so the caller abstains that ray rather than miscount).
    int CountCrossings(Vector3 p, Vector3 dir) {
        int count = 0;
        bool ok = CountRecursive(0, p, dir, ref count);
        return ok ? count : -1;
    }

    bool CountRecursive(int nodeIndex, Vector3 p, Vector3 dir, ref int count) {
        ref Node node = ref nodes[nodeIndex];
        if (!RayHitsAabb(node.Min, node.Max, p, dir))
            return true;

        if (IsLeaf(node)) {
            for (int i = node.Start; i < node.Start + node.Count; i++) {
                MeshSdfBaker.Triangle t = tris[order[i]];
                int hit = RayTriangle(p, dir, t.A, t.B, t.C);
                if (hit < 0) return false;  // degenerate hit — abstain this ray
                count += hit;
            }
            return true;
        }
        return CountRecursive(node.Left, p, dir, ref count)
             & CountRecursive(node.Start, p, dir, ref count);
    }

    // Slab test: does the (semi-infinite) ray from p along dir intersect the AABB?
    static bool RayHitsAabb(Vector3 min, Vector3 max, Vector3 p, Vector3 dir) {
        float tmin = 0f, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++) {
            float o = a == 0 ? p.X : a == 1 ? p.Y : p.Z;
            float d = a == 0 ? dir.X : a == 1 ? dir.Y : dir.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
            float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-12f) {
                if (o < lo || o > hi) return false;
            } else {
                float inv = 1f / d;
                float t1 = (lo - o) * inv, t2 = (hi - o) * inv;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
        }
        return true;
    }

    // Möller–Trumbore, ray from p along dir (t>0). Returns 1 if it crosses the triangle ahead,
    // 0 if not, -1 if the hit is within an epsilon of an edge/vertex (unreliable — abstain).
    const float Eps = 1e-7f;
    const float EdgeEps = 1e-5f;
    static int RayTriangle(Vector3 p, Vector3 dir, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 pv = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < Eps) return 0;       // parallel — no crossing
        float inv = 1f / det;
        Vector3 tv = p - a;
        float u = Vector3.Dot(tv, pv) * inv;
        Vector3 qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(dir, qv) * inv;
        float t = Vector3.Dot(e2, qv) * inv;
        if (t <= Eps) return 0;                   // behind the ray origin
        // Barycentric inside test with an edge guard: a hit grazing an edge/vertex is counted by
        // two adjacent triangles inconsistently (parity corruption), so abstain instead.
        if (u < -EdgeEps || v < -EdgeEps || u + v > 1f + EdgeEps) return 0; // clearly outside
        if (u < EdgeEps || v < EdgeEps || u + v > 1f - EdgeEps) return -1;  // on an edge — abstain
        return 1;
    }
}
