
namespace BallisticEngine.GI;

// A baked signed distance field for one mesh, sampled on a regular grid in MESH-LOCAL space.
// Distances are stored in mesh-local units (the same units as the mesh vertices); a consumer
// scales by the world transform's scale at march time. Negative = inside the solid, positive =
// outside, |value| = distance to the nearest surface.
//
// CPU-only data (BCL + OpenTK.Mathematics) so it can be produced off the GL thread and serialized
// to a .bsdf artifact. The GPU side uploads Distances into an R16F 3D texture atlas (later phase).
public sealed class MeshSdf {
    // Grid resolution per axis (cells). Distances has Res.X*Res.Y*Res.Z entries, x-fastest.
    public readonly Vector3i Res;

    // World-of-the-field bounds in mesh-local space. The field is sampled at cell CENTERS:
    // cell (i,j,k) center = BoundsMin + (i+0.5, j+0.5, k+0.5) * CellSize.
    public readonly Vector3 BoundsMin;
    public readonly Vector3 BoundsMax;
    public readonly Vector3 CellSize;

    // Signed distances, mesh-local units. Index = x + Res.X*(y + Res.Y*z).
    public readonly float[] Distances;

    public MeshSdf(Vector3i res, Vector3 boundsMin, Vector3 boundsMax, float[] distances) {
        Res = res;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Vector3 extent = boundsMax - boundsMin;
        CellSize = new Vector3(
            extent.X / MathF.Max(res.X, 1),
            extent.Y / MathF.Max(res.Y, 1),
            extent.Z / MathF.Max(res.Z, 1));
        Distances = distances;
    }

    public int Index(int x, int y, int z) => x + Res.X * (y + Res.Y * z);

    // Trilinearly samples the field at a mesh-local point, clamping to the grid. Points outside
    // the bounds read the nearest edge cell (conservative — the field flattens to the boundary
    // distance, which is correct for an exterior march that left the brick).
    public float Sample(Vector3 local) {
        // Continuous cell coordinate (cell centers sit at integer indices in this space).
        Vector3 c = (local - BoundsMin) / CellSize - new Vector3(0.5f);
        c.X = Math.Clamp(c.X, 0f, Res.X - 1.0001f);
        c.Y = Math.Clamp(c.Y, 0f, Res.Y - 1.0001f);
        c.Z = Math.Clamp(c.Z, 0f, Res.Z - 1.0001f);

        int x0 = (int)c.X, y0 = (int)c.Y, z0 = (int)c.Z;
        int x1 = Math.Min(x0 + 1, Res.X - 1);
        int y1 = Math.Min(y0 + 1, Res.Y - 1);
        int z1 = Math.Min(z0 + 1, Res.Z - 1);
        float fx = c.X - x0, fy = c.Y - y0, fz = c.Z - z0;

        float d000 = Distances[Index(x0, y0, z0)], d100 = Distances[Index(x1, y0, z0)];
        float d010 = Distances[Index(x0, y1, z0)], d110 = Distances[Index(x1, y1, z0)];
        float d001 = Distances[Index(x0, y0, z1)], d101 = Distances[Index(x1, y0, z1)];
        float d011 = Distances[Index(x0, y1, z1)], d111 = Distances[Index(x1, y1, z1)];

        float d00 = d000 + (d100 - d000) * fx;
        float d10 = d010 + (d110 - d010) * fx;
        float d01 = d001 + (d101 - d001) * fx;
        float d11 = d011 + (d111 - d011) * fx;
        float d0 = d00 + (d10 - d00) * fy;
        float d1 = d01 + (d11 - d01) * fy;
        return d0 + (d1 - d0) * fz;
    }
}
