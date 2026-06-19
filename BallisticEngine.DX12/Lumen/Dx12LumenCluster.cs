using System;
using System.Collections.Generic;
using System.Numerics;
using BallisticEngine;   // Mesh

namespace BallisticEngine.DX12;

// Lumen V2 #2A — greedy per-mesh triangle CLUSTERING for the surface-cache "RadianceCache". This is the cheap,
// parameterization-free stand-in for Unreal Lumen's mesh CARDS: instead of one radiance record per triangle
// (the cache then scales with triangle count — 2.8M on Bistro), triangles are grouped into coherent clusters of
// ~`TargetTriPerCluster`, and the cache holds ONE radiance record per cluster. The cache shrinks 30-50× and
// scales with surface complexity, not raw triangle count.
//
// Why this stays Lumen-correct for diffuse GI: diffuse indirect is LOW-FREQUENCY, so a cluster's single averaged
// radiance is visually indistinguishable from per-triangle on large/flat surfaces. The ONE quality trade is
// cluster-INTERIOR detail (a sharp colour/shadow edge inside a cluster averages out) — mitigated by clustering on
// MATERIAL (submesh) + NORMAL coherence so a cluster never straddles two materials or a hard crease.
//
// The clustering is per-MESH (cached by mesh, since one mesh can have many instances) and runs only on a Lumen
// topology rebuild (a static scene clusters once). It is O(triangles), single-pass, allocation-light.
//
// Upgrade path (the RadianceCache interface, plan #2B): a record is an opaque "surface record" index. Today a
// record == a cluster; a future real-card path swaps the record→atlas-texel mapping WITHOUT touching the
// card-light / trace / reflections shaders that consume `RecordRadiance[record]`.
internal static class Dx12LumenCluster
{
    // Triangles per cluster (the cache-size vs detail knob). Smaller = more homogeneous cluster → the single
    // representative radiance is more faithful (less hotspot on big/heterogeneous clusters); larger = smaller
    // cache. 32 is the measured sweet spot: hotspot stays low while the cache still shrinks ~10-15×.
    // BALLISTIC_DX12_LUMEN_CLUSTER overrides.
    public const int DefaultTargetTriPerCluster = 32;

    // Sıra 5 — MESH-CARD planar frame for one cluster. The cluster is normal-coherent (the clustering splits on a
    // crease), so its triangles lie ~on a plane. A card = that plane parameterized: `Origin` + orthonormal tangents
    // `U`,`V` spanning the cluster's extent, so any point P on the cluster maps to a 2D card UV in [0,1]² via
    //   u = dot(P-Origin, U) * InvExtentU,  v = dot(P-Origin, V) * InvExtentV.
    // This is the parameterization the engine's meshes lack (no lightmap UV) — derived per cluster, no authoring.
    // A texel-grid radiance card then stores per-texel radiance instead of one value per cluster → cluster-interior
    // detail (gradients, contact shadows) the single-value record flattens. Object-space (instanced via the world
    // matrix at light/sample time, same as the representative-tri path).
    public readonly struct ClusterCard
    {
        public readonly Vector3 Origin, U, V, Normal;   // object-space plane frame
        public readonly float InvExtentU, InvExtentV;    // 1/(cluster span along U,V) — maps a point to [0,1] UV
        public ClusterCard(Vector3 origin, Vector3 u, Vector3 v, Vector3 n, float ieu, float iev)
        { Origin = origin; U = u; V = v; Normal = n; InvExtentU = ieu; InvExtentV = iev; }
    }

    // The result for one mesh: a per-triangle → local-cluster-index map (length = mesh triangle count) and the
    // cluster count. Cluster indices are LOCAL to the mesh; Dx12LumenScene offsets them into a global record space.
    public readonly struct MeshClustering
    {
        public readonly int[] TriToCluster;        // [meshTriCount] → local cluster index
        public readonly int[] ClusterFirstTri;     // [clusterCount] → local triangle index of the cluster's first tri
        public readonly int ClusterCount;
        public readonly ClusterCard[] Cards;       // [clusterCount] — Sıra 5 mesh-card planar frame per cluster
        public MeshClustering(int[] triToCluster, int[] clusterFirstTri, int clusterCount, ClusterCard[] cards)
        {
            TriToCluster = triToCluster; ClusterFirstTri = clusterFirstTri; ClusterCount = clusterCount; Cards = cards;
        }
    }

    // Per-mesh cache: a mesh shared by N instances clusters once. Keyed by the Mesh reference (stable per asset).
    static readonly Dictionary<Mesh, MeshClustering> cache = new();

    static int Target =>
        int.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CLUSTER"), out int v) && v > 0
            ? v : DefaultTargetTriPerCluster;

    public static void ClearCache() => cache.Clear();

    // Cluster a mesh's triangles. Greedy single pass, RESPECTING submesh (material) boundaries: a new cluster
    // starts at every submesh boundary, when the current cluster is full, OR when the next triangle's normal
    // diverges from the cluster's running-average normal past a crease threshold (so a cluster never spans a hard
    // edge — that's where averaging would smear a shadow/colour boundary). Triangles are walked in index order,
    // which for most importers keeps spatially-adjacent triangles together (good enough; a Morton presort is a
    // later refinement if clusters end up spatially scattered).
    public static MeshClustering Cluster(Mesh mesh)
    {
        if (cache.TryGetValue(mesh, out MeshClustering hit))
            return hit;

        uint[] indices = mesh.Indices;
        Vector3[] verts = mesh.Vertices;
        int triCount = indices.Length / 3;
        var triToCluster = new int[Math.Max(triCount, 0)];

        int target = Target;
        // Only split on a HARD crease (~84°). A tighter threshold (e.g. 60°) fragments curved/detailed surfaces
        // into tiny clusters (measured 8.9 tri/cluster on Bistro vs the 96 target → little cache win). Diffuse GI
        // is low-frequency, so a cluster spanning a gentle curve is fine; only a sharp edge (where a shadow/colour
        // boundary lives) must break a cluster.
        const float creaseCos = 0.1f;   // ~84°

        int cluster = -1;
        int inCluster = 0;
        Vector3 clusterNormalSum = Vector3.Zero;
        var firstTri = new List<int>();   // local tri index where each cluster starts (its representative)

        // Submesh boundaries (a triangle index t belongs to submesh s while t < subEnd[s]). Walking submeshes in
        // order matches the index order, so we just advance a submesh cursor and force a split on each boundary.
        var subEndTri = BuildSubmeshTriEnds(mesh, triCount);
        int subCursor = 0;

        // Spatial-split state: a cluster must not span a large distance, else it groups triangles on OPPOSITE
        // sides of a thin wall (or two parallel walls) that share a normal — its single radiance then LEAKS
        // through the wall (measured: cluster leak was the dominant GI hotspot, ~6%). Split when a triangle's
        // centroid is farther from the cluster's first-triangle centroid than `spatialSplit`. The threshold is
        // sized from the mesh extent so it adapts to scene scale (big level vs a prop).
        float meshExtent = MeshExtent(verts);
        float spatialSplit = meshExtent * SpatialSplitFraction;   // a cluster spans at most this world distance
        float spatialSplitSq = spatialSplit * spatialSplit;
        Vector3 clusterSeedCentroid = Vector3.Zero;

        for (int t = 0; t < triCount; t++)
        {
            // Advance the submesh cursor; a boundary forces a fresh cluster (never mix materials in one record).
            bool submeshBoundary = false;
            while (subCursor < subEndTri.Count && t >= subEndTri[subCursor]) { subCursor++; submeshBoundary = true; }

            Vector3 n = TriNormal(verts, indices, t);
            Vector3 c = TriCentroid(verts, indices, t);

            bool startNew = cluster < 0 || inCluster >= target || submeshBoundary;
            if (!startNew && inCluster > 0)
            {
                // Crease check against the running-average normal.
                Vector3 avg = clusterNormalSum;
                float la = avg.Length();
                if (la > 1e-6f && Vector3.Dot(avg / la, n) < creaseCos)
                    startNew = true;
                // Spatial check: too far from where the cluster started → it would span a wall → split.
                else if (Vector3.DistanceSquared(c, clusterSeedCentroid) > spatialSplitSq)
                    startNew = true;
            }

            if (startNew)
            {
                cluster++;
                inCluster = 0;
                clusterNormalSum = Vector3.Zero;
                clusterSeedCentroid = c;   // the new cluster's spatial anchor
                firstTri.Add(t);   // this triangle is the new cluster's representative
            }
            triToCluster[t] = cluster;
            clusterNormalSum += n;
            inCluster++;
        }

        int clusterCount = cluster + 1;

        // === Sıra 5: build a planar CARD frame per cluster (second pass over the tri→cluster map) ===
        // For each cluster: average geometric normal N, centroid (card origin), an orthonormal tangent basis (U,V)
        // perpendicular to N, and the cluster's extent along U,V (from all its triangle vertices) → InvExtent.
        var cards = BuildCards(verts, indices, triCount, triToCluster, clusterCount);

        var result = new MeshClustering(triToCluster, firstTri.ToArray(), clusterCount, cards);
        cache[mesh] = result;
        return result;
    }

    // Per-cluster planar card frame (Sıra 5). Accumulates each cluster's normal + centroid, picks an orthonormal
    // tangent basis, then measures the cluster's [min,max] span along U,V over its triangle vertices → InvExtent.
    static ClusterCard[] BuildCards(Vector3[] verts, uint[] indices, int triCount, int[] triToCluster, int clusterCount)
    {
        var nSum = new Vector3[clusterCount];
        var cSum = new Vector3[clusterCount];
        var triCnt = new int[clusterCount];
        for (int t = 0; t < triCount; t++)
        {
            int cl = triToCluster[t];
            nSum[cl] += TriNormal(verts, indices, t);
            cSum[cl] += TriCentroid(verts, indices, t);
            triCnt[cl]++;
        }

        var cards = new ClusterCard[clusterCount];
        var basisU = new Vector3[clusterCount];
        var basisV = new Vector3[clusterCount];
        var origin = new Vector3[clusterCount];
        var normal = new Vector3[clusterCount];
        for (int cl = 0; cl < clusterCount; cl++)
        {
            int cnt = Math.Max(triCnt[cl], 1);
            Vector3 n = nSum[cl];
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
            Vector3 o = cSum[cl] / cnt;
            // Orthonormal tangent basis perpendicular to n (Duff et al. branchless frame would do; a simple
            // up-cross is fine here and stable since n is well-defined for a normal-coherent cluster).
            Vector3 up = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 u = Vector3.Normalize(Vector3.Cross(up, n));
            Vector3 v = Vector3.Cross(n, u);
            normal[cl] = n; origin[cl] = o; basisU[cl] = u; basisV[cl] = v;
        }

        // Extent pass: project every triangle vertex of each cluster onto its (U,V) and track min/max.
        var minU = new float[clusterCount]; var maxU = new float[clusterCount];
        var minV = new float[clusterCount]; var maxV = new float[clusterCount];
        for (int cl = 0; cl < clusterCount; cl++) { minU[cl] = minV[cl] = float.MaxValue; maxU[cl] = maxV[cl] = -float.MaxValue; }
        for (int t = 0; t < triCount; t++)
        {
            int cl = triToCluster[t];
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = verts[indices[t * 3 + k]] - origin[cl];
                float pu = Vector3.Dot(p, basisU[cl]);
                float pv = Vector3.Dot(p, basisV[cl]);
                if (pu < minU[cl]) minU[cl] = pu; if (pu > maxU[cl]) maxU[cl] = pu;
                if (pv < minV[cl]) minV[cl] = pv; if (pv > maxV[cl]) maxV[cl] = pv;
            }
        }
        for (int cl = 0; cl < clusterCount; cl++)
        {
            // Card origin = the cluster's (min U, min V) corner so card UV is [0,1] across the cluster. Extent is the
            // span; a degenerate (zero-span) axis falls back to a tiny extent so InvExtent stays finite.
            float spanU = MathF.Max(maxU[cl] - minU[cl], 1e-4f);
            float spanV = MathF.Max(maxV[cl] - minV[cl], 1e-4f);
            Vector3 cornerOrigin = origin[cl] + basisU[cl] * minU[cl] + basisV[cl] * minV[cl];
            cards[cl] = new ClusterCard(cornerOrigin, basisU[cl], basisV[cl], normal[cl], 1f / spanU, 1f / spanV);
        }
        return cards;
    }

    static List<int> BuildSubmeshTriEnds(Mesh mesh, int triCount)
    {
        var ends = new List<int>();
        if (mesh.SubMeshes != null)
            foreach (var sm in mesh.SubMeshes)
                ends.Add((sm.IndexStart + sm.IndexCount) / 3);
        if (ends.Count == 0) ends.Add(triCount);
        return ends;
    }

    static Vector3 TriNormal(Vector3[] verts, uint[] indices, int tri)
    {
        uint i0 = indices[tri * 3 + 0], i1 = indices[tri * 3 + 1], i2 = indices[tri * 3 + 2];
        Vector3 p0 = verts[i0], p1 = verts[i1], p2 = verts[i2];
        Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
        float l = n.Length();
        return l > 1e-12f ? n / l : Vector3.UnitY;
    }

    static Vector3 TriCentroid(Vector3[] verts, uint[] indices, int tri) =>
        (verts[indices[tri * 3 + 0]] + verts[indices[tri * 3 + 1]] + verts[indices[tri * 3 + 2]]) * (1f / 3f);

    // A cluster spans at most this fraction of the mesh's bounding-box diagonal — caps how far apart triangles in
    // one record may be, so a record never bridges a thin wall (front+back) or two parallel walls. Small enough to
    // stop leaks, large enough to keep clusters near the tri-count target on big flat surfaces. Env-tunable.
    static float SpatialSplitFraction =>
        float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CLUSTER_SPAN"), out float v) && v > 0
            ? v : 0.03f;

    // Bounding-box diagonal of the mesh in object space (the scale the spatial split is relative to).
    static float MeshExtent(Vector3[] verts)
    {
        if (verts.Length == 0) return 1f;
        Vector3 lo = verts[0], hi = verts[0];
        for (int i = 1; i < verts.Length; i++) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
        float d = (hi - lo).Length();
        return d > 1e-4f ? d : 1f;
    }
}
