namespace BallisticEngine.AssetPipeline.Sdf;

/// <summary>
/// Offline per-mesh card-representation generator (FAZ 3a of the Lumen GI port). Produces a small set
/// of oriented bounding-box "cards" (UE <c>FLumenCardOBB</c>) that a later surface cache will capture
/// and light — the mesh-local equivalent of what UE5 builds in <c>MeshCardRepresentationUtilities.cpp</c>.
///
/// ALGORITHM (a faithful adaptation of UE5.3, with the existing <see cref="MeshSdf"/> standing in for
/// Embree surface ray-casts — SDF surface voxels ARE the surfels):
///
/// 1. SURFEL EXTRACTION — every voxel whose |distance| sits in a one-voxel band around the
///    zero-isosurface is a surfel. Its outward normal is the central-difference gradient of the field;
///    its dominant axis-aligned DIRECTION (0..5, order -X+X-Y+Y-Z+Z) is the gradient's largest component
///    with its sign. A surfel is kept only when its normal agrees with that direction's normal beyond
///    <see cref="NormalThreshold"/> (UE's coherence cut — skip grazing voxels). Surfels are bucketed by
///    direction.
///
/// 2. CLUSTERING — per direction, surfels are flooded into spatially-contiguous clusters over the voxel
///    grid: neighbours share the direction, are adjacent in the projected (U,V) plane, AND lie within a
///    few voxels in depth (so a near wall and a far wall facing the same way become SEPARATE cards, like
///    UE's near-plane layering). A cluster survives only when it covers <see cref="MinClusterCoverage"/>
///    voxels AND its projected density clears <see cref="MinDensityPerCluster"/> — thin/sparse clusters
///    are rejected.
///
/// 3. OBB EMIT — each surviving cluster's mesh-local AABB becomes a <see cref="MeshCard"/>: the two
///    non-dominant axes span the capture plane, the dominant axis (with the outward sign) is the view
///    normal, the depth extent is padded half a voxel each side (UE near/far margin) and clamped to the
///    grid.
///
/// 4. CAP — all clusters across the 6 directions are sorted by weighted coverage and the top
///    <paramref name="maxCards"/> kept (clamped to [1, <see cref="MaxCardsPerMesh"/>]); the drop count is
///    logged, never silently truncated.
///
/// Surfel extraction is parallelized across z-slices like <see cref="MeshSdfBuilder"/>; the clustering
/// is single-threaded.
/// </summary>
public static class MeshCardBuilder {
    // --- VERBATIM UE5.3 constants (MeshCardRepresentationUtilities.cpp). ---
    public const int MaxCardsPerMesh = 32;
    const float NormalThreshold = 0.25f;
    const float MinDensity = 0.2f;
    const float MinDensityPerCluster = MinDensity / 3f; // ≈ 0.0667
    const float MinClusterCoverage = 15.0f;

    // SDF surfel band: a voxel is on the surface when |distance| is within ~one voxel of the isosurface.
    const float SurfelBandVoxels = 1.0f;

    // Flood-fill neighbour reach in the projected plane / depth (voxels). UV adjacency within 1 voxel
    // (8-neighbourhood), depth within 2 voxels — keeps near/far walls as distinct cards.
    const int UvNeighbourReach = 1;
    const int DepthNeighbourReach = 2;

    /// <summary>
    /// Generates the card representation for <paramref name="mesh"/> from its offline SDF. Returns null
    /// when the mesh has no valid SDF or when no cluster survives the validity cuts.
    /// <paramref name="maxCards"/> is the per-mesh card budget (clamped to [1, <see cref="MaxCardsPerMesh"/>]).
    /// </summary>
    public static MeshCards Generate(in MeshData mesh, int maxCards = 12) =>
        Generate(mesh.Sdf, maxCards);

    /// <summary>
    /// Generates the card representation directly from an explicit <paramref name="sdf"/> (Lumen FAZ 8.6:
    /// the per-submesh path passes each component's own submesh-local SDF here). Cards come out in the
    /// SAME space as the SDF grid. Returns null when the SDF is invalid or no cluster survives.
    /// </summary>
    public static MeshCards Generate(MeshSdf sdf, int maxCards = 12, bool quiet = false) {
        if (sdf is null || !sdf.IsValid)
            return null;

        maxCards = Math.Clamp(maxCards, 1, MaxCardsPerMesh);

        int resX = sdf.ResX, resY = sdf.ResY, resZ = sdf.ResZ;
        Vector3 vs = sdf.VoxelSize;
        float surfelBand = SurfelBandVoxels * MathF.Max(vs.X, MathF.Max(vs.Y, vs.Z));

        // --- Step 2: surfel extraction (parallel over z-slices, merged after). ---
        var sliceSurfels = new List<Surfel>[resZ];
        Parallel.For(0, resZ, z => {
            var local = new List<Surfel>();
            for (int y = 0; y < resY; y++) {
                for (int x = 0; x < resX; x++) {
                    int idx = sdf.Index(x, y, z);
                    if (MathF.Abs(sdf.Distances[idx]) > surfelBand)
                        continue;

                    Vector3 p = sdf.VoxelCenter(x, y, z);
                    // Central-difference gradient (the outward surface normal).
                    Vector3 n = new(
                        sdf.Sample(p + new Vector3(vs.X, 0f, 0f)) - sdf.Sample(p - new Vector3(vs.X, 0f, 0f)),
                        sdf.Sample(p + new Vector3(0f, vs.Y, 0f)) - sdf.Sample(p - new Vector3(0f, vs.Y, 0f)),
                        sdf.Sample(p + new Vector3(0f, 0f, vs.Z)) - sdf.Sample(p - new Vector3(0f, 0f, vs.Z)));
                    float len = n.Length();
                    if (len < 1e-8f)
                        continue; // flat/degenerate gradient — skip.
                    n /= len;

                    // Dominant axis-aligned direction whose normal best matches the gradient.
                    float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
                    int axis = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
                    float comp = axis == 0 ? n.X : axis == 1 ? n.Y : n.Z;
                    int sign = comp >= 0f ? +1 : -1;
                    int dirIndex = axis * 2 + (sign > 0 ? 1 : 0);

                    // UE normal-coherence cut: keep only surfels facing their direction beyond the threshold.
                    Vector3 dirNormal = DirectionNormal(dirIndex);
                    if (Vector3.Dot(n, dirNormal) < NormalThreshold)
                        continue;

                    local.Add(new Surfel { X = x, Y = y, Z = z, Direction = dirIndex });
                }
            }
            sliceSurfels[z] = local;
        });

        // Bucket surfels by direction; index them by grid cell for O(1) neighbour lookup.
        var buckets = new List<Surfel>[6];
        for (int d = 0; d < 6; d++) buckets[d] = new List<Surfel>();
        // cellOwner[idx] = direction+1 of the surfel at that voxel (0 = none). A voxel can be a surfel
        // for exactly one direction (its dominant one), so one byte per cell is enough.
        var cellDir = new byte[(long)resX * resY * resZ <= int.MaxValue ? resX * resY * resZ : 0];
        if (cellDir.Length == 0)
            return null;
        foreach (List<Surfel> slice in sliceSurfels) {
            if (slice is null) continue;
            foreach (Surfel s in slice) {
                buckets[s.Direction].Add(s);
                cellDir[sdf.Index(s.X, s.Y, s.Z)] = (byte)(s.Direction + 1);
            }
        }

        // --- Step 3: per-direction clustering + step 4 OBB emit. ---
        var clusters = new List<Cluster>();
        var assigned = new bool[cellDir.Length];

        for (int dir = 0; dir < 6; dir++) {
            int axis = dir / 2;               // dominant (depth) axis
            int uAxis, vAxis;                  // the two non-dominant (capture-plane) axes
            NonDominantAxes(axis, out uAxis, out vAxis);

            foreach (Surfel seed in buckets[dir]) {
                int seedIdx = sdf.Index(seed.X, seed.Y, seed.Z);
                if (assigned[seedIdx])
                    continue;

                // Flood from the seed over same-direction adjacent surfels (UV within reach AND depth
                // within reach — so a near wall and a far wall facing the same way stay separate).
                var cluster = new Cluster {
                    MinX = seed.X, MinY = seed.Y, MinZ = seed.Z,
                    MaxX = seed.X, MaxY = seed.Y, MaxZ = seed.Z,
                };
                var stack = new Stack<Surfel>();
                stack.Push(seed);
                assigned[seedIdx] = true;

                while (stack.Count > 0) {
                    Surfel s = stack.Pop();
                    cluster.Coverage += 1;
                    cluster.MinX = Math.Min(cluster.MinX, s.X); cluster.MaxX = Math.Max(cluster.MaxX, s.X);
                    cluster.MinY = Math.Min(cluster.MinY, s.Y); cluster.MaxY = Math.Max(cluster.MaxY, s.Y);
                    cluster.MinZ = Math.Min(cluster.MinZ, s.Z); cluster.MaxZ = Math.Max(cluster.MaxZ, s.Z);

                    // Visit the local neighbourhood: ±UvNeighbourReach in the two plane axes,
                    // ±DepthNeighbourReach along the depth axis.
                    for (int du = -UvNeighbourReach; du <= UvNeighbourReach; du++)
                    for (int dv = -UvNeighbourReach; dv <= UvNeighbourReach; dv++)
                    for (int dw = -DepthNeighbourReach; dw <= DepthNeighbourReach; dw++) {
                        if (du == 0 && dv == 0 && dw == 0) continue;
                        int nx = s.X, ny = s.Y, nz = s.Z;
                        AddAlong(uAxis, du, ref nx, ref ny, ref nz);
                        AddAlong(vAxis, dv, ref nx, ref ny, ref nz);
                        AddAlong(axis, dw, ref nx, ref ny, ref nz);
                        if ((uint)nx >= (uint)resX || (uint)ny >= (uint)resY || (uint)nz >= (uint)resZ)
                            continue;
                        int nIdx = sdf.Index(nx, ny, nz);
                        if (assigned[nIdx] || cellDir[nIdx] != (byte)(dir + 1))
                            continue;
                        assigned[nIdx] = true;
                        stack.Push(new Surfel { X = nx, Y = ny, Z = nz, Direction = dir });
                    }
                }

                // --- Validity: enough voxels AND dense enough in the projected rectangle. ---
                int uSpan = AxisSpan(cluster, uAxis) + 1;
                int vSpan = AxisSpan(cluster, vAxis) + 1;
                float faceArea = (float)uSpan * vSpan; // projected rectangle area in voxels
                float density = faceArea > 0f ? cluster.Coverage / faceArea : 0f;
                if (cluster.Coverage < MinClusterCoverage || density <= MinDensityPerCluster)
                    continue;

                // Weighted coverage: SDF surfels have uniform visibility, so weight == coverage
                // (UE weights by per-surfel visibility; documented simplification).
                cluster.WeightedCoverage = cluster.Coverage;
                cluster.Direction = dir;
                clusters.Add(cluster);
            }
        }

        if (clusters.Count == 0)
            return null;

        // --- Step 5: cap to maxCards by weighted coverage (UE LimitClusters). ---
        clusters.Sort((a, b) => b.WeightedCoverage.CompareTo(a.WeightedCoverage));
        int kept = Math.Min(maxCards, clusters.Count);
        int dropped = clusters.Count - kept;

        var cards = new MeshCard[kept];
        for (int i = 0; i < kept; i++)
            cards[i] = EmitCard(sdf, clusters[i]);

        if (!quiet) {
            var perDir = new int[6];
            for (int i = 0; i < kept; i++) perDir[cards[i].DirectionIndex]++;
            Debugging.Log(
                $"[Cards] {kept} cards kept ({dropped} dropped of {clusters.Count} clusters) — " +
                $"per-direction [-X {perDir[0]}, +X {perDir[1]}, -Y {perDir[2]}, +Y {perDir[3]}, -Z {perDir[4]}, +Z {perDir[5]}]");
        }

        return new MeshCards(cards);
    }

    /// <summary>Builds the mesh-local OBB <see cref="MeshCard"/> from a clustered voxel AABB.</summary>
    static MeshCard EmitCard(MeshSdf sdf, Cluster c) {
        Vector3 min = sdf.VoxelCenter(c.MinX, c.MinY, c.MinZ);
        Vector3 max = sdf.VoxelCenter(c.MaxX, c.MaxY, c.MaxZ);
        Vector3 center = (min + max) * 0.5f;
        Vector3 halfExtent = (max - min) * 0.5f;
        Vector3 vs = sdf.VoxelSize;

        int axis = c.Direction / 2;          // depth axis
        int sign = (c.Direction & 1) != 0 ? +1 : -1;
        NonDominantAxes(axis, out int uAxis, out int vAxis);

        Vector3 axisZ = DirectionNormal(c.Direction); // outward view normal
        Vector3 axisX = UnitAxis(uAxis);
        Vector3 axisY = UnitAxis(vAxis);

        // Right-handed frame: AxisX × AxisY must equal AxisZ; flip AxisY if not.
        if (Vector3.Dot(Vector3.Cross(axisX, axisY), axisZ) < 0f)
            axisY = -axisY;

        // Extents along (AxisX, AxisY, AxisZ). The non-dominant pair maps directly to grid axes; the
        // depth extent (AxisZ) is the dominant axis half-extent, padded half a voxel each side and
        // clamped so the card never pokes outside the grid bounds.
        float extX = AxisComponent(halfExtent, uAxis);
        float extY = AxisComponent(halfExtent, vAxis);
        float extZ = AxisComponent(halfExtent, axis) + 0.5f * AxisComponent(vs, axis);

        // Clamp the depth half-extent to the grid (center ± extZ must stay inside).
        float gridMin = AxisComponent(sdf.GridOrigin, axis);
        float gridMax = gridMin + AxisComponent(sdf.GridExtent, axis);
        float centerD = AxisComponent(center, axis);
        float maxExtZ = MathF.Min(centerD - gridMin, gridMax - centerD);
        if (maxExtZ > 0f) extZ = MathF.Min(extZ, maxExtZ);

        _ = sign; // sign already baked into axisZ via DirectionNormal.
        return new MeshCard(center, axisX, axisY, axisZ, new Vector3(extX, extY, extZ), c.Direction);
    }

    // --- direction / axis helpers (order -X+X-Y+Y-Z+Z) ---

    static Vector3 DirectionNormal(int dirIndex) {
        int axis = dirIndex / 2;
        float sign = (dirIndex & 1) != 0 ? +1f : -1f;
        return axis == 0 ? new Vector3(sign, 0f, 0f)
             : axis == 1 ? new Vector3(0f, sign, 0f)
                         : new Vector3(0f, 0f, sign);
    }

    static Vector3 UnitAxis(int axis) =>
        axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

    static void NonDominantAxes(int axis, out int uAxis, out int vAxis) {
        // The two grid axes that are NOT the dominant one, in ascending order.
        uAxis = axis == 0 ? 1 : 0;
        vAxis = axis == 2 ? 1 : 2;
    }

    static void AddAlong(int axis, int delta, ref int x, ref int y, ref int z) {
        if (axis == 0) x += delta;
        else if (axis == 1) y += delta;
        else z += delta;
    }

    static int AxisSpan(Cluster c, int axis) =>
        axis == 0 ? c.MaxX - c.MinX : axis == 1 ? c.MaxY - c.MinY : c.MaxZ - c.MinZ;

    static float AxisComponent(Vector3 v, int axis) =>
        axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    struct Surfel {
        public int X, Y, Z;
        public int Direction;
    }

    sealed class Cluster {
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int Direction;
        public float Coverage;
        public float WeightedCoverage;
    }
}
