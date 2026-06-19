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

    // The result for one mesh: a per-triangle → local-cluster-index map (length = mesh triangle count) and the
    // cluster count. Cluster indices are LOCAL to the mesh; Dx12LumenScene offsets them into a global record space.
    public readonly struct MeshClustering
    {
        public readonly int[] TriToCluster;        // [meshTriCount] → local cluster index
        public readonly int[] ClusterFirstTri;     // [clusterCount] → local triangle index of the cluster's first tri
        public readonly int ClusterCount;
        public MeshClustering(int[] triToCluster, int[] clusterFirstTri, int clusterCount)
        {
            TriToCluster = triToCluster; ClusterFirstTri = clusterFirstTri; ClusterCount = clusterCount;
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

        for (int t = 0; t < triCount; t++)
        {
            // Advance the submesh cursor; a boundary forces a fresh cluster (never mix materials in one record).
            bool submeshBoundary = false;
            while (subCursor < subEndTri.Count && t >= subEndTri[subCursor]) { subCursor++; submeshBoundary = true; }

            Vector3 n = TriNormal(verts, indices, t);

            bool startNew = cluster < 0 || inCluster >= target || submeshBoundary;
            if (!startNew && inCluster > 0)
            {
                // Crease check against the running-average normal.
                Vector3 avg = clusterNormalSum;
                float la = avg.Length();
                if (la > 1e-6f && Vector3.Dot(avg / la, n) < creaseCos)
                    startNew = true;
            }

            if (startNew)
            {
                cluster++;
                inCluster = 0;
                clusterNormalSum = Vector3.Zero;
                firstTri.Add(t);   // this triangle is the new cluster's representative
            }
            triToCluster[t] = cluster;
            clusterNormalSum += n;
            inCluster++;
        }

        var result = new MeshClustering(triToCluster, firstTri.ToArray(), cluster + 1);
        cache[mesh] = result;
        return result;
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
}
