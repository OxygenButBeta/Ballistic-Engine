
namespace BallisticEngine;

// CPU-side terrain height field. Carries no GPU state, so it can be produced off the GL thread
// and fed to TerrainMeshBuilder. The heights are a row-major Resolution x Resolution grid in
// normalized [0,1] units; the world height of a sample is Heights[i] * HeightScale, and the grid
// spans Size (world XZ) centered on the entity origin. A fresh terrain is flat (all zeros).
public readonly struct TerrainData {
    public readonly int Resolution;     // vertices per side (>= 2); cells per side = Resolution - 1
    public readonly Vector2 Size;       // world-space extent on X and Z
    public readonly float HeightScale;  // world height of a height value of 1.0
    public readonly float[] Heights;    // row-major, length Resolution * Resolution, values in [0,1]

    public TerrainData(int resolution, Vector2 size, float heightScale, float[] heights) {
        Resolution = resolution;
        Size = size;
        HeightScale = heightScale;
        Heights = heights;
    }

    public bool IsValid =>
        Resolution >= 2 && Heights is not null && Heights.Length == Resolution * Resolution &&
        Size.X > 0f && Size.Y > 0f;

    // A flat terrain with all heights at zero — the default a freshly created .terrain asset holds.
    public static TerrainData Flat(int resolution, Vector2 size, float heightScale) =>
        new(resolution, size, heightScale, new float[resolution * resolution]);
}
