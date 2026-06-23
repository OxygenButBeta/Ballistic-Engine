namespace BallisticEngine.AssetPipeline.Sdf;

/// <summary>
/// Offline per-mesh signed distance field generator (FAZ 1 of the Lumen GI port).
///
/// ALGORITHM (the standard one used by geometry3Sharp / SideFX WindingNumber / UE):
///
/// 1. UNSIGNED distance — a triangle AABB BVH (median split on the longest axis of the centroid
///    bounds) gives the nearest-triangle distance per query point. Per-triangle distance is the
///    exact closest-point-on-triangle (Ericson, "Real-Time Collision Detection", §5.1.5). The BVH
///    walk is a best-first descent with a running min: a node whose AABB is farther than the best
///    distance found so far is pruned, so the average query is ~O(log n).
///
/// 2. SIGN — the GENERALIZED (solid-angle) winding number, NOT ray parity. Ray parity fails on the
///    open / inverted / self-intersecting "triangle soup" that real scenes (Bistro) are made of;
///    the winding number degrades gracefully. We use the Barill et al. 2018 ("Fast Winding Numbers
///    for Soups and Clouds") BVH-accelerated form: each BVH node caches an aggregate dipole (the
///    area-weighted sum of triangle normals and an area-weighted centroid). For a query point far
///    from a node (distance > beta * node radius) the node's whole subtree is approximated by that
///    single dipole term; near the node we recurse to the exact per-triangle solid angle (Van
///    Oosterom–Strackee formula). This turns the naive O(voxels * tris) into roughly
///    O(voxels * log tris). w ~ 1 inside, ~ 0 outside; we threshold at 0.5 and flip the unsigned
///    distance to negative when inside.
///
/// ACCURACY / PERF TRADEOFF (honest): the dipole is only the first (monopole/dipole) term of the
/// multipole expansion — Barill's paper carries it further. With beta = 2.0 the first-order term is
/// accurate to well under the 0.5 inside/outside threshold for the far field, which is all the SIGN
/// needs (we never use w as a continuous quantity, only sign(w - 0.5)). The UNSIGNED distance is
/// exact (closest point on the true triangle), so surface accuracy is bounded only by voxel size.
///
/// Generation runs on the importing thread (its own thread, not the frame JobSystem) and is
/// parallelized across voxels with Parallel.For.
/// </summary>
public static class MeshSdfBuilder {
    // Total-voxel cap: keeps Bistro-scale import tractable. ~512k voxels * (BVH query) is a few
    // hundred ms per mesh parallelized. When the requested resolution would exceed this, the whole
    // grid is uniformly downscaled and we LOG it — no silent truncation.
    const int MaxVoxels = 512 * 1024;

    // Pad the AABB by this many voxels on every side so the zero-isosurface has slack.
    const int PaddingVoxels = 3;

    // Barill far-field acceptance ratio: approximate a node by its dipole when the query distance
    // exceeds Beta * node bounding radius. Larger = more exact recursion = slower but tighter.
    const float Beta = 2.0f;

    /// <summary>
    /// Generates a dense SDF for the LOD0 geometry of <paramref name="mesh"/>. Returns null only for
    /// degenerate input (no triangles). <paramref name="maxResolution"/> is the voxel count along the
    /// longest axis before the global voxel cap is applied.
    /// </summary>
    public static MeshSdf Generate(in MeshData mesh, int maxResolution = 64) {
        if (!mesh.IsValid || mesh.Indices.Length < 3)
            return null;

        Vector3[] verts = mesh.Vertices;

        // --- LOD0 triangle list (SubMeshData.IndexStart/IndexCount; LOD0 == the base index range).
        // We build from every submesh's base range so split-by-nodes parts all contribute.
        List<int> triA = new(), triB = new(), triC = new();
        CollectLod0Triangles(mesh, triA, triB, triC);
        return GenerateFromTriangles(verts, triA, triB, triC, maxResolution);
    }

    /// <summary>
    /// PER-SUBMESH SDF (Lumen FAZ 8.6): builds an SDF over ONLY <paramref name="sub"/>'s LOD0 triangle
    /// range, in that submesh's LOCAL space (mesh-local vertices pre-transformed by inverse(NodeTransform)
    /// — the same convention MeshCollider uses). Each Bistro component thus gets a small, tight grid, so
    /// the global 512k voxel cap never engages and cards can actually be placed. Returns null for a
    /// degenerate/empty range. The caller supplies <paramref name="maxResolution"/> (kept low per submesh).
    /// </summary>
    public static MeshSdf GenerateForSubMesh(in MeshData mesh, in SubMeshData sub, int maxResolution = 32) {
        if (!mesh.IsValid) return null;
        uint[] idx = mesh.Indices;
        Vector3[] meshVerts = mesh.Vertices;

        LodRange lod0 = sub.LodAt(0);
        int start = lod0.FirstIndex, count = lod0.IndexCount;
        int end = start + count;
        if (start < 0 || end > idx.Length) { start = sub.IndexStart; end = Math.Min(idx.Length, start + sub.IndexCount); }
        if (end - start < 3) return null;

        // Transform this submesh's vertices into SUBMESH-LOCAL space (mesh-local -> submesh-local).
        // We build a compact remapped vertex list so the BVH only covers this component's verts.
        Matrix4 inverseNode = Matrix4.Identity;
        if (MathF.Abs(sub.NodeTransform.GetDeterminant()) > 1e-12f &&
            Matrix4.Invert(sub.NodeTransform, out Matrix4 inv))
            inverseNode = inv;

        var remap = new Dictionary<int, int>(capacity: (end - start) / 2);
        var localVerts = new List<Vector3>(capacity: (end - start) / 2);
        List<int> triA = new(), triB = new(), triC = new();
        int Local(int meshVertIndex) {
            if (!remap.TryGetValue(meshVertIndex, out int li)) {
                li = localVerts.Count;
                localVerts.Add(Vector3.Transform(meshVerts[meshVertIndex], inverseNode));
                remap[meshVertIndex] = li;
            }
            return li;
        }
        for (int i = start; i + 2 < end; i += 3) {
            triA.Add(Local((int)idx[i]));
            triB.Add(Local((int)idx[i + 1]));
            triC.Add(Local((int)idx[i + 2]));
        }
        if (triA.Count == 0) return null;
        return GenerateFromTriangles(localVerts.ToArray(), triA, triB, triC, maxResolution);
    }

    /// <summary>Core: builds a dense SDF from a triangle index list over <paramref name="verts"/>.</summary>
    static MeshSdf GenerateFromTriangles(Vector3[] verts, List<int> triA, List<int> triB, List<int> triC,
        int maxResolution) {
        int triCount = triA.Count;
        if (triCount == 0)
            return null;

        // --- Mesh AABB over the referenced vertices.
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int t = 0; t < triCount; t++) {
            Expand(ref min, ref max, verts[triA[t]]);
            Expand(ref min, ref max, verts[triB[t]]);
            Expand(ref min, ref max, verts[triC[t]]);
        }
        Vector3 size = max - min;
        // Degenerate-thin guard: give zero-extent axes a hair of width so voxel math stays finite.
        float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (longest <= 0f) longest = 1f;
        size = Vector3.Max(size, new Vector3(longest * 1e-4f));

        // Cubic voxels: voxel edge = longest axis / maxResolution.
        float voxel = longest / Math.Max(1, maxResolution);

        // Pad symmetrically.
        Vector3 pad = new(voxel * PaddingVoxels);
        Vector3 origin = min - pad;
        Vector3 extent = size + pad * 2f;

        int resX = Math.Max(1, (int)MathF.Ceiling(extent.X / voxel));
        int resY = Math.Max(1, (int)MathF.Ceiling(extent.Y / voxel));
        int resZ = Math.Max(1, (int)MathF.Ceiling(extent.Z / voxel));

        // --- Global voxel cap: uniformly downscale resolution to fit MaxVoxels.
        long total = (long)resX * resY * resZ;
        if (total > MaxVoxels) {
            double scale = Math.Cbrt((double)MaxVoxels / total);
            int nx = Math.Max(1, (int)(resX * scale));
            int ny = Math.Max(1, (int)(resY * scale));
            int nz = Math.Max(1, (int)(resZ * scale));
            // Shrink-to-fit (the cbrt rounds down each axis, so this stays under the cap).
            while ((long)nx * ny * nz > MaxVoxels) {
                if (nx >= ny && nx >= nz && nx > 1) nx--;
                else if (ny >= nz && ny > 1) ny--;
                else if (nz > 1) nz--;
                else break;
            }
            Debugging.LogWarning(
                $"[SDF] mesh too large for {resX}x{resY}x{resZ} ({total} voxels); " +
                $"downscaled to {nx}x{ny}x{nz} ({(long)nx * ny * nz} voxels, cap {MaxVoxels}).");
            resX = nx; resY = ny; resZ = nz;
        }

        // Recompute the exact extent so VoxelSize lines up with the chosen resolution.
        // (extent already covers the padded bounds; keep it — voxel sizes become slightly anisotropic
        //  after the cap, which Sample()/VoxelCenter() handle via per-axis VoxelSize.)

        // --- Build BVH over the triangles.
        var bvh = new TriangleBvh(verts, triA, triB, triC);

        // --- Evaluate every voxel center in parallel.
        var distances = new float[(long)resX * resY * resZ <= int.MaxValue ? resX * resY * resZ : 0];
        if (distances.Length == 0)
            return null; // can't happen under the cap, but keeps the analyzer happy.

        Vector3 vs = new(extent.X / resX, extent.Y / resY, extent.Z / resZ);

        Parallel.For(0, resZ, z => {
            for (int y = 0; y < resY; y++) {
                int rowBase = resX * (y + resY * z);
                for (int x = 0; x < resX; x++) {
                    Vector3 p = origin + new Vector3((x + 0.5f) * vs.X, (y + 0.5f) * vs.Y, (z + 0.5f) * vs.Z);
                    float unsigned = MathF.Sqrt(bvh.ClosestDistanceSq(p));
                    float winding = bvh.WindingNumber(p, Beta);
                    // Inside ⇒ |w| ≈ 1, outside ⇒ |w| ≈ 0. We threshold on the MAGNITUDE so the
                    // sign is correct regardless of the mesh's triangle orientation (CW or CCW),
                    // which the renderer cannot assume across a soup of imported parts.
                    distances[rowBase + x] = MathF.Abs(winding) > 0.5f ? -unsigned : unsigned;
                }
            }
        });

        return new MeshSdf(origin, extent, resX, resY, resZ, distances);
    }

    static void CollectLod0Triangles(in MeshData mesh, List<int> a, List<int> b, List<int> c) {
        uint[] idx = mesh.Indices;
        SubMeshData[] subs = mesh.SubMeshes;
        if (subs is { Length: > 0 }) {
            foreach (SubMeshData sm in subs) {
                // LOD0 = the submesh base range (LodAt(0) returns IndexStart/IndexCount).
                LodRange lod0 = sm.LodAt(0);
                int start = lod0.FirstIndex;
                int count = lod0.IndexCount;
                int end = start + count;
                if (start < 0 || end > idx.Length) { start = sm.IndexStart; end = Math.Min(idx.Length, start + sm.IndexCount); }
                for (int i = start; i + 2 < end; i += 3) {
                    a.Add((int)idx[i]); b.Add((int)idx[i + 1]); c.Add((int)idx[i + 2]);
                }
            }
        }
        else {
            for (int i = 0; i + 2 < idx.Length; i += 3) {
                a.Add((int)idx[i]); b.Add((int)idx[i + 1]); c.Add((int)idx[i + 2]);
            }
        }
    }

    static void Expand(ref Vector3 min, ref Vector3 max, Vector3 v) {
        min = Vector3.Min(min, v);
        max = Vector3.Max(max, v);
    }

    // ---------------------------------------------------------------------------------------------
    // Triangle AABB BVH with Barill fast-winding dipole aggregates.
    // ---------------------------------------------------------------------------------------------
    sealed class TriangleBvh {
        struct Node {
            public Vector3 Min, Max;       // tight AABB of the node's triangles
            public Vector3 DipoleCenter;   // area-weighted centroid (winding expansion point)
            public Vector3 DipoleNormal;   // area-weighted sum of triangle (un-normalized) normals
            public float Radius;           // distance from DipoleCenter to the farthest triangle vertex
            public int Start, Count;       // [Start, Start+Count) into the reordered triangle index list
            public int Left, Right;        // child node indices, or -1 for a leaf
        }

        const int LeafSize = 4;

        readonly Vector3[] _verts;
        readonly int[] _ia, _ib, _ic;   // per-triangle vertex indices
        readonly int[] _order;          // BVH-reordered triangle ids
        readonly Vector3[] _centroid;   // triangle centroids (for splitting)
        readonly Node[] _nodes;
        int _nodeCount;

        public TriangleBvh(Vector3[] verts, List<int> a, List<int> b, List<int> c) {
            _verts = verts;
            _ia = a.ToArray(); _ib = b.ToArray(); _ic = c.ToArray();
            int n = _ia.Length;
            _order = new int[n];
            _centroid = new Vector3[n];
            for (int i = 0; i < n; i++) {
                _order[i] = i;
                _centroid[i] = (verts[_ia[i]] + verts[_ib[i]] + verts[_ic[i]]) / 3f;
            }
            // A binary tree over n leaves has < 2n nodes.
            _nodes = new Node[Math.Max(1, 2 * n)];
            _nodeCount = 0;
            Build(0, n);
        }

        // Returns the index of the node covering triangles [start, end).
        int Build(int start, int end) {
            int nodeIndex = _nodeCount++;
            ref Node node = ref _nodes[nodeIndex];
            node.Start = start;
            node.Count = end - start;
            node.Left = node.Right = -1;

            // Tight AABB + dipole aggregate over this range.
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            Vector3 weightedCentroid = Vector3.Zero;
            Vector3 normalSum = Vector3.Zero;
            float areaSum = 0f;
            for (int i = start; i < end; i++) {
                int t = _order[i];
                Vector3 v0 = _verts[_ia[t]], v1 = _verts[_ib[t]], v2 = _verts[_ic[t]];
                Expand(ref min, ref max, v0); Expand(ref min, ref max, v1); Expand(ref min, ref max, v2);
                Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0); // = 2*area*normal (un-normalized)
                float area = cross.Length() * 0.5f;
                Vector3 c = (v0 + v1 + v2) / 3f;
                weightedCentroid += c * area;
                normalSum += cross * 0.5f; // area-weighted normal == 0.5 * cross
                areaSum += area;
            }
            node.Min = min; node.Max = max;
            node.DipoleCenter = areaSum > 1e-20f ? weightedCentroid / areaSum : (min + max) * 0.5f;
            node.DipoleNormal = normalSum;

            float radiusSq = 0f;
            for (int i = start; i < end; i++) {
                int t = _order[i];
                radiusSq = MathF.Max(radiusSq, Vector3.DistanceSquared(node.DipoleCenter, _verts[_ia[t]]));
                radiusSq = MathF.Max(radiusSq, Vector3.DistanceSquared(node.DipoleCenter, _verts[_ib[t]]));
                radiusSq = MathF.Max(radiusSq, Vector3.DistanceSquared(node.DipoleCenter, _verts[_ic[t]]));
            }
            node.Radius = MathF.Sqrt(radiusSq);

            if (node.Count <= LeafSize)
                return nodeIndex;

            // Median split on the longest axis of the CENTROID bounds.
            Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
            for (int i = start; i < end; i++) Expand(ref cmin, ref cmax, _centroid[_order[i]]);
            Vector3 ext = cmax - cmin;
            int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
            if (Axis(ext, axis) <= 0f)
                return nodeIndex; // all centroids coincident: keep as a (larger) leaf.

            int mid = (start + end) / 2;
            NthElement(start, end, mid, axis);

            int left = Build(start, mid);
            int right = Build(mid, end);
            _nodes[nodeIndex].Left = left;
            _nodes[nodeIndex].Right = right;
            return nodeIndex;
        }

        static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

        // Quickselect partition so _order[mid] is in sorted position by centroid[axis].
        void NthElement(int start, int end, int nth, int axis) {
            int lo = start, hi = end - 1;
            while (lo < hi) {
                float pivot = Axis(_centroid[_order[(lo + hi) / 2]], axis);
                int i = lo, j = hi;
                while (i <= j) {
                    while (Axis(_centroid[_order[i]], axis) < pivot) i++;
                    while (Axis(_centroid[_order[j]], axis) > pivot) j--;
                    if (i <= j) { (_order[i], _order[j]) = (_order[j], _order[i]); i++; j--; }
                }
                if (nth <= j) hi = j;
                else if (nth >= i) lo = i;
                else break;
            }
        }

        // --- Unsigned distance² to the nearest triangle (best-first BVH descent with pruning).
        public float ClosestDistanceSq(Vector3 p) {
            float best = float.MaxValue;
            ClosestRecurse(0, p, ref best);
            return best;
        }

        void ClosestRecurse(int nodeIndex, Vector3 p, ref float best) {
            ref Node node = ref _nodes[nodeIndex];
            if (AabbDistanceSq(p, node.Min, node.Max) >= best)
                return;

            if (node.Left < 0) {
                for (int i = node.Start; i < node.Start + node.Count; i++) {
                    int t = _order[i];
                    float d = PointTriangleDistanceSq(p, _verts[_ia[t]], _verts[_ib[t]], _verts[_ic[t]]);
                    if (d < best) best = d;
                }
                return;
            }

            // Descend into the nearer child first so the running min prunes the far one.
            float dl = AabbDistanceSq(p, _nodes[node.Left].Min, _nodes[node.Left].Max);
            float dr = AabbDistanceSq(p, _nodes[node.Right].Min, _nodes[node.Right].Max);
            if (dl <= dr) {
                ClosestRecurse(node.Left, p, ref best);
                ClosestRecurse(node.Right, p, ref best);
            }
            else {
                ClosestRecurse(node.Right, p, ref best);
                ClosestRecurse(node.Left, p, ref best);
            }
        }

        // --- Generalized winding number (Barill 2018 BVH-accelerated dipole approximation).
        public float WindingNumber(Vector3 p, float beta) {
            return WindingRecurse(0, p, beta) / (4f * MathF.PI);
        }

        // Returns the accumulated SOLID ANGLE (before the 1/4pi). Far nodes use the dipole term;
        // near nodes recurse to exact per-triangle solid angles.
        float WindingRecurse(int nodeIndex, Vector3 p, float beta) {
            ref Node node = ref _nodes[nodeIndex];
            Vector3 d = node.DipoleCenter - p;
            float dist = d.Length();

            // Far field: single dipole term  w ~ (n . r) / (4pi |r|^3), here returning the solid angle
            // (the /4pi is applied once at the top). Dipole solid angle = (n . d) / |d|^3.
            if (node.Left >= 0 && dist > beta * node.Radius && dist > 1e-12f) {
                float inv = 1f / (dist * dist * dist);
                return Vector3.Dot(node.DipoleNormal, d) * inv;
            }

            if (node.Left < 0) {
                float sum = 0f;
                for (int i = node.Start; i < node.Start + node.Count; i++) {
                    int t = _order[i];
                    sum += TriangleSolidAngle(p, _verts[_ia[t]], _verts[_ib[t]], _verts[_ic[t]]);
                }
                return sum;
            }

            return WindingRecurse(node.Left, p, beta) + WindingRecurse(node.Right, p, beta);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Geometry primitives.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Signed solid angle of triangle (a,b,c) as seen from p — Van Oosterom–Strackee formula.</summary>
    static float TriangleSolidAngle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 va = a - p, vb = b - p, vc = c - p;
        float la = va.Length(), lb = vb.Length(), lc = vc.Length();
        if (la < 1e-20f || lb < 1e-20f || lc < 1e-20f)
            return 0f; // p coincides with a vertex.
        float numer = Vector3.Dot(va, Vector3.Cross(vb, vc));
        float denom = la * lb * lc
                      + Vector3.Dot(va, vb) * lc
                      + Vector3.Dot(vb, vc) * la
                      + Vector3.Dot(vc, va) * lb;
        // atan2 of the half-angle, times 2 → the signed solid angle in [-2pi, 2pi].
        return 2f * MathF.Atan2(numer, denom);
    }

    /// <summary>Squared distance from point p to the closest point on triangle (a,b,c). Ericson §5.1.5.</summary>
    static float PointTriangleDistanceSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return Vector3.DistanceSquared(p, a); // vertex region A

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return Vector3.DistanceSquared(p, b); // vertex region B

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return Vector3.DistanceSquared(p, c); // vertex region C

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
            float v = d1 / (d1 - d3);
            Vector3 q = a + v * ab; // edge AB
            return Vector3.DistanceSquared(p, q);
        }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
            float w = d2 / (d2 - d6);
            Vector3 q = a + w * ac; // edge AC
            return Vector3.DistanceSquared(p, q);
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) {
            float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            Vector3 q = b + w * (c - b); // edge BC
            return Vector3.DistanceSquared(p, q);
        }

        // Face region — barycentric interior.
        float denom = 1f / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        Vector3 closest = a + ab * vv + ac * ww;
        return Vector3.DistanceSquared(p, closest);
    }

    static float AabbDistanceSq(Vector3 p, Vector3 min, Vector3 max) {
        float dx = p.X < min.X ? min.X - p.X : p.X > max.X ? p.X - max.X : 0f;
        float dy = p.Y < min.Y ? min.Y - p.Y : p.Y > max.Y ? p.Y - max.Y : 0f;
        float dz = p.Z < min.Z ? min.Z - p.Z : p.Z > max.Z ? p.Z - max.Z : 0f;
        return dx * dx + dy * dy + dz * dz;
    }

    // -------------------------------------------------------------------------------------------------
    // Validation hook (callable from a test / bal verb). Returns a human-readable report and a pass flag.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sanity-checks a generated SDF against simple invariants:
    ///   - a point far outside the bounds has a large POSITIVE distance,
    ///   - the field's most-negative voxel is interior (negative) for a solid-ish mesh,
    ///   - distances are finite.
    /// Returns true when all checks pass; <paramref name="report"/> always describes the outcome.
    /// </summary>
    public static bool Validate(MeshSdf sdf, out string report) {
        if (sdf is null || !sdf.IsValid) {
            report = "SDF is null or invalid (empty/mismatched grid).";
            return false;
        }

        bool ok = true;
        var sb = new System.Text.StringBuilder();

        // 1. Finiteness.
        float minD = float.MaxValue, maxD = float.MinValue;
        bool finite = true;
        foreach (float d in sdf.Distances) {
            if (!float.IsFinite(d)) { finite = false; break; }
            if (d < minD) minD = d;
            if (d > maxD) maxD = d;
        }
        if (!finite) { ok = false; sb.AppendLine("FAIL: SDF contains non-finite distances."); }
        else sb.AppendLine($"distance range [{minD:F4}, {maxD:F4}]");

        // 2. Far-outside point is positive and large.
        Vector3 far = sdf.GridOrigin - sdf.GridExtent; // a corner well outside the padded box
        float farD = sdf.Sample(far);
        // Sample() clamps into the grid, so this reads the nearest boundary voxel — it must be > 0.
        if (farD <= 0f) { ok = false; sb.AppendLine($"FAIL: boundary sample is non-positive ({farD:F4})."); }
        else sb.AppendLine($"boundary sample positive ({farD:F4}) OK");

        // 3. There exists interior (negative) signal — true for any closed-ish region.
        if (minD >= 0f) sb.AppendLine("WARN: no negative voxels (mesh may be a thin/open shell).");
        else sb.AppendLine($"interior present (min {minD:F4}) OK");

        report = sb.ToString().TrimEnd();
        return ok;
    }
}
