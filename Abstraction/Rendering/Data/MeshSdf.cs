
namespace BallisticEngine;

/// <summary>
/// CPU-side signed distance field for a single mesh, generated offline at import time.
/// This is the foundation for Lumen's software ray tracing and the global distance field:
/// each voxel stores the SIGNED distance (in mesh-local / world units, since the grid lives in
/// the same space as <see cref="MeshData.Vertices"/>) to the nearest triangle surface — negative
/// inside the (generalized-winding) volume, positive outside.
///
/// Indexing convention: x is fastest, then y, then z.
///   index(x, y, z) = x + ResX * (y + ResY * z)
/// Voxel CENTERS are sampled. The center of voxel (x,y,z) in mesh space is:
///   GridOrigin + (new Vector3(x, y, z) + 0.5) * VoxelSize
/// where VoxelSize = GridExtent / new Vector3(ResX, ResY, ResZ).
///
/// <see cref="GridOrigin"/> is the MIN corner of the padded bounds; <see cref="GridExtent"/> is the
/// full world size of the grid (max corner = GridOrigin + GridExtent). The mesh AABB is padded by a
/// few voxels on each side so the zero-isosurface has slack around it (a tight grid would clip the
/// band right at the surface).
/// </summary>
public sealed class MeshSdf {
    public Vector3 GridOrigin;
    public Vector3 GridExtent;
    public int ResX;
    public int ResY;
    public int ResZ;

    /// <summary>Signed distance per voxel center, length ResX*ResY*ResZ, x-fastest (see class docs).</summary>
    public float[] Distances;

    public MeshSdf() { }

    public MeshSdf(Vector3 gridOrigin, Vector3 gridExtent, int resX, int resY, int resZ, float[] distances) {
        GridOrigin = gridOrigin;
        GridExtent = gridExtent;
        ResX = resX;
        ResY = resY;
        ResZ = resZ;
        Distances = distances;
    }

    public int VoxelCount => ResX * ResY * ResZ;

    /// <summary>World-space size of one voxel along each axis (slightly anisotropic in general).</summary>
    public Vector3 VoxelSize =>
        new(GridExtent.X / ResX, GridExtent.Y / ResY, GridExtent.Z / ResZ);

    public int Index(int x, int y, int z) => x + ResX * (y + ResY * z);

    public bool IsValid =>
        Distances is { Length: > 0 } && ResX > 0 && ResY > 0 && ResZ > 0 &&
        Distances.Length == ResX * ResY * ResZ;

    /// <summary>Mesh-space center of voxel (x,y,z).</summary>
    public Vector3 VoxelCenter(int x, int y, int z) {
        Vector3 vs = VoxelSize;
        return GridOrigin + new Vector3((x + 0.5f) * vs.X, (y + 0.5f) * vs.Y, (z + 0.5f) * vs.Z);
    }

    /// <summary>
    /// Trilinearly samples the field at an arbitrary mesh-space point (clamped to the grid).
    /// Used by debug visualizers and the sanity check; the runtime GPU path will upload
    /// <see cref="Distances"/> as a 3D texture and sample it in HLSL.
    /// </summary>
    public float Sample(Vector3 p) {
        Vector3 vs = VoxelSize;
        // Convert to continuous voxel-center space: center of voxel i sits at coordinate i.
        float fx = (p.X - GridOrigin.X) / vs.X - 0.5f;
        float fy = (p.Y - GridOrigin.Y) / vs.Y - 0.5f;
        float fz = (p.Z - GridOrigin.Z) / vs.Z - 0.5f;

        fx = Math.Clamp(fx, 0f, ResX - 1f);
        fy = Math.Clamp(fy, 0f, ResY - 1f);
        fz = Math.Clamp(fz, 0f, ResZ - 1f);

        int x0 = (int)MathF.Floor(fx); int x1 = Math.Min(x0 + 1, ResX - 1);
        int y0 = (int)MathF.Floor(fy); int y1 = Math.Min(y0 + 1, ResY - 1);
        int z0 = (int)MathF.Floor(fz); int z1 = Math.Min(z0 + 1, ResZ - 1);
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;

        float c000 = Distances[Index(x0, y0, z0)];
        float c100 = Distances[Index(x1, y0, z0)];
        float c010 = Distances[Index(x0, y1, z0)];
        float c110 = Distances[Index(x1, y1, z0)];
        float c001 = Distances[Index(x0, y0, z1)];
        float c101 = Distances[Index(x1, y0, z1)];
        float c011 = Distances[Index(x0, y1, z1)];
        float c111 = Distances[Index(x1, y1, z1)];

        float c00 = c000 + (c100 - c000) * tx;
        float c10 = c010 + (c110 - c010) * tx;
        float c01 = c001 + (c101 - c001) * tx;
        float c11 = c011 + (c111 - c011) * tx;
        float c0 = c00 + (c10 - c00) * ty;
        float c1 = c01 + (c11 - c01) * ty;
        return c0 + (c1 - c0) * tz;
    }
}
