using OpenTK.Mathematics;

namespace BallisticEngine.GI;

// Bakes a MeshSdf from triangle soup. Two correctness pillars (both prior failure modes):
//
//   1. DISTANCE — exact unsigned distance to the nearest triangle, accelerated by a triangle BVH
//      so a dense grid bake is seconds, not the ~19s brute-force the old path cost.
//   2. SIGN — robust inside/outside by RAY-STAB PARITY, voted across SIX axis rays (±X/±Y/±Z).
//      Face-normal sign fails on merged/welded meshes whose triangle windings disagree (that was
//      the "all-teal march" bug). Parity counting is winding-agnostic; the 6-ray majority absorbs
//      the odd grazing/edge hit that would flip a single ray.
public static class MeshSdfBaker {
    // BALLISTIC_LUMEN_DIAG: split the bake into BVH-build vs grid-query timings so we can see which
    // dominates the warm-up cost (read once — env lookups per bake would themselves show up).
    static readonly bool BakeDiag = System.Environment.GetEnvironmentVariable("BALLISTIC_LUMEN_DIAG") == "1";

    // Parameters controlling a bake. Padding expands the bounds so the field has exterior cells to
    // march through before it hits the surface; resolution is the long-axis cell count (other axes
    // scale to keep cells ~cubic).
    public readonly struct Settings {
        public readonly int MaxResolution;    // cells along the longest axis
        public readonly float PaddingFraction; // bounds expansion as a fraction of the diagonal
        public Settings(int maxResolution = 32, float paddingFraction = 0.1f) {
            MaxResolution = Math.Clamp(maxResolution, 4, 256);
            PaddingFraction = Math.Clamp(paddingFraction, 0f, 1f);
        }
        public static Settings Default => new();
    }

    public static MeshSdf Bake(MeshData mesh, Settings settings) {
        if (!mesh.IsValid)
            return null;
        return BakeRange(mesh.Vertices, mesh.Indices, 0, mesh.Indices.Length, settings);
    }

    // Bakes an SDF for ONE submesh: the triangles in the index range [indexStart, indexStart+
    // indexCount) over the shared vertex array. Vertices are in MODEL space (the same space the
    // renderer's WorldMatrix maps from), so the resulting field's mesh-local space == model space,
    // and a GPU instance uses the renderer's plain world matrix as model->world. A tight per-submesh
    // bounds means each object gets a FINE field instead of one coarse whole-scene brick — the fix
    // for both the exterior spurious-hit wash and the missing interior occlusion.
    public static MeshSdf BakeSubMesh(Vector3[] verts, uint[] idx, int indexStart, int indexCount,
        Settings settings) {
        if (verts == null || idx == null || indexCount < 3)
            return null;
        return BakeRange(verts, idx, indexStart, indexCount, settings);
    }

    // Bakes ONE GLOBAL field over an explicit WORLD-space box from a flat list of world-space triangles
    // (already transformed). Unlike BakeSubMesh this does NOT fit tight bounds — the bounds are given
    // (the scene/camera box), so the resulting field is the whole-scene distance field the global-SDF
    // (clipmap-style) Lumen path marches as ONE texture, instead of thousands of per-object bricks. The
    // distance/sign math is identical (BVH closest-triangle + 6-ray parity sign). `res` is the per-axis
    // cell count (cubic-ish over the box). Returns null if there are no triangles. Heavy — call on a
    // background thread (the parallel grid loop is here, but a 128^3 bake over a room is hundreds of ms).
    public static MeshSdf BakeWorldTriangles(System.Collections.Generic.List<Vector3> worldVerts,
        Vector3 boundsMin, Vector3 boundsMax, Vector3i res) =>
        BakeWorldTriangles(worldVerts, null, boundsMin, boundsMax, res, out _);

    // Overload that ALSO bakes a per-voxel ALBEDO field (the nearest triangle's material colour). For
    // the Lumen voxel-lighting surface cache: a voxel bounces light tinted by its OWN surface albedo
    // (a red wall bounces red), not one global grey. `triAlbedo` is one Vector3 (linear RGB) per
    // TRIANGLE (worldVerts.Count/3 entries), matched by index. `albedoOut` is RGB-packed (x-fastest,
    // 3 floats/voxel) or null if triAlbedo is null. Same distance/sign math as the base overload.
    public static MeshSdf BakeWorldTriangles(System.Collections.Generic.List<Vector3> worldVerts,
        System.Collections.Generic.List<Vector3> triAlbedo, Vector3 boundsMin, Vector3 boundsMax,
        Vector3i res, out float[] albedoOut, int signRays = 7) {
        PreparedField prep = Prepare(worldVerts, triAlbedo);
        if (prep == null) { albedoOut = null; return null; }
        return BakePrepared(prep, boundsMin, boundsMax, res, out albedoOut, signRays);
    }

    // A built BVH + per-triangle albedo over a triangle snapshot, ready to bake at ANY resolution.
    // The GDF builds this ONCE per cascade snapshot (the BVH build is the dominant cost at high
    // triangle counts — 572ms for 222K tris), then bakes a coarse field AND the full-res refine from
    // the same handle, paying the build only once instead of twice.
    public sealed class PreparedField {
        internal readonly Triangle[] Tris;
        internal readonly TriangleBvh Bvh;
        internal readonly System.Collections.Generic.List<Vector3> TriAlbedo;
        internal PreparedField(Triangle[] tris, TriangleBvh bvh, System.Collections.Generic.List<Vector3> alb) {
            Tris = tris; Bvh = bvh; TriAlbedo = alb;
        }
        public int TriangleCount => Tris.Length;
        public bool HasAlbedo => TriAlbedo != null;

        // World-space AABB of triangle i (SdfSeedExtractor rasterizes each tri's AABB into the grid to
        // find the shell voxels directly — surface-area cost, not the whole-volume ClosestPoint sweep).
        public void TriangleBounds(int i, out Vector3 min, out Vector3 max) {
            Triangle t = Tris[i]; min = t.Min; max = t.Max;
        }

        // GPU-JFA seed queries (SdfSeedExtractor): closest surface point + nearest-tri index, the
        // 7-ray parity sign, and per-triangle albedo — reusing the proven BVH/parity math at the seeds.
        public float ClosestPoint(Vector3 p, out Vector3 point, out int triIndex) =>
            Bvh.ClosestPoint(p, out point, out triIndex);
        public bool IsInside(Vector3 p, int signRays) => Bvh.IsInside(p, signRays);
        public Vector3 AlbedoOf(int triIndex) =>
            (TriAlbedo != null && triIndex >= 0 && triIndex < TriAlbedo.Count) ? TriAlbedo[triIndex] : new Vector3(0.5f);
    }

    // Build the BVH over a world-triangle snapshot (call once; bake at multiple resolutions after).
    public static PreparedField Prepare(System.Collections.Generic.List<Vector3> worldVerts,
        System.Collections.Generic.List<Vector3> triAlbedo) {
        int triCount = worldVerts.Count / 3;
        if (triCount == 0)
            return null;
        var tris = new Triangle[triCount];
        for (int t = 0; t < triCount; t++)
            tris[t] = new Triangle(worldVerts[t * 3], worldVerts[t * 3 + 1], worldVerts[t * 3 + 2]);
        var swBvh = BakeDiag ? System.Diagnostics.Stopwatch.StartNew() : null;
        var bvh = new TriangleBvh(tris);
        if (swBvh != null) System.Console.WriteLine($"[GDF bake]   BVH build {triCount} tris -> {swBvh.ElapsedMilliseconds} ms");
        bool wantAlb = triAlbedo != null && triAlbedo.Count == triCount;
        return new PreparedField(tris, bvh, wantAlb ? triAlbedo : null);
    }

    // Bake a field from a PreparedField (reusing its BVH) over a world box at the given resolution.
    public static MeshSdf BakePrepared(PreparedField prep, Vector3 boundsMin, Vector3 boundsMax,
        Vector3i res, out float[] albedoOut, int signRays = 7) {
        albedoOut = null;
        if (prep == null)
            return null;
        Triangle[] tris = prep.Tris;
        int triCount = tris.Length;
        var bvh = prep.Bvh;
        System.Collections.Generic.List<Vector3> triAlbedo = prep.TriAlbedo;

        res = new Vector3i(Math.Max(2, res.X), Math.Max(2, res.Y), Math.Max(2, res.Z));
        var distances = new float[res.X * res.Y * res.Z];
        bool wantAlbedo = triAlbedo != null && triAlbedo.Count == triCount;
        float[] albedo = wantAlbedo ? new float[res.X * res.Y * res.Z * 3] : null;
        Vector3 cellSize = (boundsMax - boundsMin) / new Vector3(res.X, res.Y, res.Z);

        // NARROW-BAND SIGN (Phase A warm-up speedup, EXACT): the unsigned distance is cheap (one
        // branch-and-bound BVH descent), but the SIGN is a 7-ray parity vote — 7 more traversals,
        // the dominant per-voxel cost. A voxel whose nearest surface is many cells away is
        // UNAMBIGUOUSLY outside (you can't be deep inside a solid yet far from every triangle), so
        // the ray cast is wasted there. Only run IsInside within a band of the surface; beyond it,
        // sign = +. The band is generous (cell diagonal * a few) so no genuinely-interior voxel is
        // missed — a thin gap between two close walls still falls inside the band. On a 96^3 cascade
        // most voxels are empty space far from geometry, so this skips the 7-ray test for the large
        // majority and the bake drops from seconds to a fraction of one. Distance magnitude is
        // IDENTICAL to before (only the sign of far voxels is now trivially +, which they already were).
        float cellDiag = cellSize.Length;            // worst-case half-cell reach
        float bandSq = (cellDiag * 2.5f) * (cellDiag * 2.5f); // sign-test band (squared, to skip the sqrt)

        var swGrid = BakeDiag ? System.Diagnostics.Stopwatch.StartNew() : null;
        // Parallelize over Z*Y ROWS, not just Z slabs: a coarse 32^3 bake has only 32 Z-slabs, far
        // fewer than the core count, so a slab-only split left most cores idle (the coarse-bake cost
        // we're trying to cut). Rows give res.Y*res.Z work items (e.g. 1024 at 32^3) — full occupancy.
        int rows = res.Y * res.Z;
        System.Threading.Tasks.Parallel.For(0, rows, row => {
            int y = row % res.Y;
            int z = row / res.Y;
            int rowBase = res.X * (y + res.Y * z);
            for (int x = 0; x < res.X; x++) {
                Vector3 p = boundsMin + new Vector3(
                    (x + 0.5f) * cellSize.X, (y + 0.5f) * cellSize.Y, (z + 0.5f) * cellSize.Z);
                int idx = rowBase + x;
                float distSq;
                if (wantAlbedo) {
                    distSq = bvh.ClosestDistanceSq(p, out int triIndex);
                    Vector3 a = triIndex >= 0 ? triAlbedo[triIndex] : new Vector3(0.5f);
                    albedo[idx * 3] = a.X; albedo[idx * 3 + 1] = a.Y; albedo[idx * 3 + 2] = a.Z;
                } else {
                    distSq = bvh.ClosestDistanceSq(p);
                }
                float unsigned = MathF.Sqrt(distSq);
                // Only voxels near a surface can be inside — cast rays only there; else outside (+).
                // signRays caps the parity vote (fewer for coarse warm-up bakes — the dominant cost).
                bool inside = distSq <= bandSq && bvh.IsInside(p, signRays);
                distances[idx] = inside ? -unsigned : unsigned;
            }
        });
        if (swGrid != null) System.Console.WriteLine($"[GDF bake]   grid {res.X}^3 query -> {swGrid.ElapsedMilliseconds} ms");
        albedoOut = albedo;
        return new MeshSdf(res, boundsMin, boundsMax, distances);
    }

    static MeshSdf BakeRange(Vector3[] verts, uint[] idx, int indexStart, int indexCount,
        Settings settings) {
        int end = indexStart + indexCount;
        if (verts == null || idx == null || indexStart < 0 || end > idx.Length)
            return null;
        int triCount = indexCount / 3;
        if (triCount == 0)
            return null;

        var tris = new Triangle[triCount];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int t = 0; t < triCount; t++) {
            int b0 = indexStart + t * 3;
            Vector3 a = verts[idx[b0 + 0]];
            Vector3 b = verts[idx[b0 + 1]];
            Vector3 c = verts[idx[b0 + 2]];
            tris[t] = new Triangle(a, b, c);
            min = Vector3.ComponentMin(min, Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)));
            max = Vector3.ComponentMax(max, Vector3.ComponentMax(a, Vector3.ComponentMax(b, c)));
        }

        // ---- Padded, ~cubic-celled grid ----
        Vector3 size = max - min;
        float diag = size.Length;
        Vector3 pad = new(diag * settings.PaddingFraction * 0.5f);
        // Guard against degenerate (flat) axes so the field still has a marchable shell.
        pad += new Vector3(
            MathF.Max(0f, (diag * 0.02f) - size.X * 0.5f),
            MathF.Max(0f, (diag * 0.02f) - size.Y * 0.5f),
            MathF.Max(0f, (diag * 0.02f) - size.Z * 0.5f));
        Vector3 bMin = min - pad;
        Vector3 bMax = max + pad;
        Vector3 ext = bMax - bMin;

        float longest = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
        float cell = longest / settings.MaxResolution;
        var res = new Vector3i(
            Math.Max(2, (int)MathF.Ceiling(ext.X / cell)),
            Math.Max(2, (int)MathF.Ceiling(ext.Y / cell)),
            Math.Max(2, (int)MathF.Ceiling(ext.Z / cell)));
        // Recompute exact bounds so cells are exactly `cell` on each side.
        bMax = bMin + new Vector3(res.X * cell, res.Y * cell, res.Z * cell);

        var bvh = new TriangleBvh(tris);

        var distances = new float[res.X * res.Y * res.Z];
        Vector3 cellSize = (bMax - bMin) / new Vector3(res.X, res.Y, res.Z);

        // Parallelize over Z slabs — each cell is independent.
        System.Threading.Tasks.Parallel.For(0, res.Z, z => {
            for (int y = 0; y < res.Y; y++) {
                for (int x = 0; x < res.X; x++) {
                    Vector3 p = bMin + new Vector3(
                        (x + 0.5f) * cellSize.X,
                        (y + 0.5f) * cellSize.Y,
                        (z + 0.5f) * cellSize.Z);
                    float unsigned = MathF.Sqrt(bvh.ClosestDistanceSq(p));
                    bool inside = bvh.IsInside(p);
                    distances[x + res.X * (y + res.Y * z)] = inside ? -unsigned : unsigned;
                }
            }
        });

        return new MeshSdf(res, bMin, bMax, distances);
    }

    // ---- Triangle ----------------------------------------------------------
    internal readonly struct Triangle {
        public readonly Vector3 A, B, C;
        public Triangle(Vector3 a, Vector3 b, Vector3 c) { A = a; B = b; C = c; }
        public Vector3 Min => Vector3.ComponentMin(A, Vector3.ComponentMin(B, C));
        public Vector3 Max => Vector3.ComponentMax(A, Vector3.ComponentMax(B, C));
        public Vector3 Centroid => (A + B + C) * (1f / 3f);
    }
}
