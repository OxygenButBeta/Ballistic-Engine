using OpenTK.Mathematics;

namespace BallisticEngine.GI;

// GPU JUMP-FLOOD SDF — the CPU SEED stage (Phase 1).
//
// The full-res CPU bake (MeshSdfBaker.BakePrepared) is ~1s per 96^3 cascade — too slow to keep the
// Global Distance Field fine under camera motion, so the field is permanently stuck coarse and the
// coarse distance field's blocky hit/miss structure is the GI speckle. The fix is a GPU jump-flood
// (JFA) field: cheap CPU SEEDS on the thin surface shell, then a GPU flood fills the whole volume.
//
// This extractor produces, for the SHELL voxels only (those within ~1.5 cells of geometry), the data
// the GPU flood needs:
//   * the closest SURFACE POINT (in continuous voxel coordinates) — the seed JFA propagates,
//   * the voxel's SIGN (from the proven TriangleBvh 7-ray parity — winding-agnostic on welded soup,
//     so this REUSES the exact sign math already verified against analytic SDFs; no new sign method,
//     no all-teal-class risk),
//   * the nearest triangle's ALBEDO (linear RGB) — the Lumen surface-cache colour the flood carries.
//
// Cost is proportional to SURFACE AREA (shell cells), not volume — a few ms even at 96^3, vs the ~1s
// whole-grid CPU bake. The GPU then propagates nearest-seed over the empty interior/exterior in
// log2(res) flood passes and resolves signed distance = |voxel - nearestSeedPoint| * sign(nearestSeed).
//
// CPU-only (BCL + OpenTK.Mathematics) so it runs on the GDF's background bake task.
public static class SdfSeedExtractor {
    // A flat seed grid (res^3, x-fastest, MeshSdf index convention) ready to upload to the GPU.
    public sealed class SeedGrid {
        public Vector3i Res;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public Vector3 CellSize;
        // Per voxel (x-fastest): the JFA seed payload.
        //   SeedPos[i]  — closest surface point in CONTINUOUS VOXEL coords (xyz), or (-1,-1,-1) if no seed.
        //   SeedSign[i] — +1 outside, -1 inside (only meaningful where SeedPos is valid).
        //   Albedo[i*3..] — nearest triangle albedo (linear RGB), 0 where no seed.
        public Vector4[] SeedPos;  // xyz = surface point in voxel coords, w = sign (+1/-1), or w=0 => no seed
        public float[] Albedo;     // RGB, 3 floats/voxel
        public int SeedCount;      // number of valid seed voxels (diagnostic)
    }

    // Extract seeds over `box` at `res` from the prepared field. `signRays` caps the parity vote (7 is
    // the robust full vote). `bandCells` is the shell thickness in cells: a voxel is a seed if its
    // closest surface is within bandCells * cellDiag (generous so no thin gap between close walls is
    // missed). Returns a SeedGrid; voxels outside the band have w=0 (the GPU flood fills them).
    //
    // TRIANGLE-DRIVEN (the perf fix): instead of querying ClosestPoint for ALL res^3 voxels (which is
    // as slow as the full CPU bake — it was ~1s at 96^3), we RASTERIZE each triangle's AABB (expanded
    // by the band) into the grid to mark CANDIDATE voxels, then compute the seed data (closest point +
    // parity sign + albedo) only for those candidates. Cost is proportional to SURFACE AREA, not volume.
    public static SeedGrid Extract(MeshSdfBaker.PreparedField prep, Vector3 boundsMin, Vector3 boundsMax,
        Vector3i res, int signRays = 7, float bandCells = 1.75f) {
        res = new Vector3i(System.Math.Max(2, res.X), System.Math.Max(2, res.Y), System.Math.Max(2, res.Z));
        var grid = new SeedGrid {
            Res = res, BoundsMin = boundsMin, BoundsMax = boundsMax,
            CellSize = (boundsMax - boundsMin) / new Vector3(res.X, res.Y, res.Z),
            SeedPos = new Vector4[res.X * res.Y * res.Z],
            Albedo = new float[res.X * res.Y * res.Z * 3],
        };
        if (prep == null || prep.TriangleCount == 0)
            return grid;

        Vector3 cellSize = grid.CellSize;
        float cellDiag = cellSize.Length;
        float band = bandCells * cellDiag;
        float bandSq = band * band;
        bool wantAlbedo = prep.HasAlbedo;
        Vector3 invCell = new(1f / cellSize.X, 1f / cellSize.Y, 1f / cellSize.Z);

        // ---- Pass 1: mark candidate voxels by rasterizing each triangle's band-expanded AABB ----
        // A byte flag per voxel (0 = not a candidate). Parallel over triangles; the writes are idempotent
        // (set to 1), so concurrent writes to the same flag are safe without locking.
        var candidate = new byte[res.X * res.Y * res.Z];
        Vector3 bandVec = new(band);
        System.Threading.Tasks.Parallel.For(0, prep.TriangleCount, t => {
            prep.TriangleBounds(t, out Vector3 tmin, out Vector3 tmax);
            // Triangle AABB -> grid index range, expanded by the band so near-but-not-overlapping voxels
            // (within bandCells of the surface) are also candidates.
            Vector3 lo = (tmin - bandVec - boundsMin) * invCell;
            Vector3 hi = (tmax + bandVec - boundsMin) * invCell;
            int x0 = System.Math.Max(0, (int)MathF.Floor(lo.X)), x1 = System.Math.Min(res.X - 1, (int)MathF.Ceiling(hi.X));
            int y0 = System.Math.Max(0, (int)MathF.Floor(lo.Y)), y1 = System.Math.Min(res.Y - 1, (int)MathF.Ceiling(hi.Y));
            int z0 = System.Math.Max(0, (int)MathF.Floor(lo.Z)), z1 = System.Math.Min(res.Z - 1, (int)MathF.Ceiling(hi.Z));
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++) {
                    int rowBase = res.X * (y + res.Y * z);
                    for (int x = x0; x <= x1; x++)
                        candidate[rowBase + x] = 1;
                }
        });

        // ---- Pass 2: compute seed data (closest point + sign + albedo) for candidate voxels only ----
        int seedCount = 0;
        var seedLock = new object();
        int rows = res.Y * res.Z;
        System.Threading.Tasks.Parallel.For(0, rows, () => 0, (row, _, localCount) => {
            int y = row % res.Y;
            int z = row / res.Y;
            int rowBase = res.X * (y + res.Y * z);
            for (int x = 0; x < res.X; x++) {
                int idx = rowBase + x;
                if (candidate[idx] == 0) { grid.SeedPos[idx] = Vector4.Zero; continue; }
                Vector3 p = boundsMin + new Vector3(
                    (x + 0.5f) * cellSize.X, (y + 0.5f) * cellSize.Y, (z + 0.5f) * cellSize.Z);
                float distSq = prep.ClosestPoint(p, out Vector3 surf, out int triIndex);
                // The AABB rasterization is conservative — confirm the voxel is actually within the band.
                if (distSq > bandSq) { grid.SeedPos[idx] = Vector4.Zero; continue; }
                // Surface point -> CONTINUOUS VOXEL coords (cell centers at integer indices, matching
                // MeshSdf.Sample) so the GPU flood propagates a grid-space coordinate.
                Vector3 surfVox = (surf - boundsMin) / cellSize - new Vector3(0.5f);
                // SIGN: the proven 7-ray parity at the voxel center (winding-agnostic, robust on welded
                // soup). The flood propagates this seed's sign to interior/exterior voxels (constant
                // within a region). This is the seed-extraction cost; bounded by a tight band (1.75 cells).
                float sign = prep.IsInside(p, signRays) ? -1f : 1f;
                grid.SeedPos[idx] = new Vector4(surfVox.X, surfVox.Y, surfVox.Z, sign);
                if (wantAlbedo) {
                    Vector3 a = prep.AlbedoOf(triIndex);
                    grid.Albedo[idx * 3] = a.X; grid.Albedo[idx * 3 + 1] = a.Y; grid.Albedo[idx * 3 + 2] = a.Z;
                }
                localCount++;
            }
            return localCount;
        }, localCount => { lock (seedLock) seedCount += localCount; });

        grid.SeedCount = seedCount;
        return grid;
    }
}
