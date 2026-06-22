
namespace BallisticEngine;

public readonly struct TerrainData {
    public readonly int Resolution;
    public readonly Vector2 Size;
    public readonly float HeightScale;
    public readonly float[] Heights;

    public TerrainData(int resolution, Vector2 size, float heightScale, float[] heights) {
        Resolution = resolution;
        Size = size;
        HeightScale = heightScale;
        Heights = heights;
    }

    public bool IsValid =>
        Resolution >= 2 && Heights is not null && Heights.Length == Resolution * Resolution &&
        Size.X > 0f && Size.Y > 0f;

    public static TerrainData Flat(int resolution, Vector2 size, float heightScale) =>
        new(resolution, size, heightScale, new float[resolution * resolution]);
}
