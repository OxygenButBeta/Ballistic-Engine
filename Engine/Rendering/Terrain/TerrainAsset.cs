
namespace BallisticEngine;

public sealed class TerrainAsset : BObject {
    public int Resolution { get; }
    public Vector2 Size { get; private set; }
    public float HeightScale { get; private set; }
    public float[] Heights { get; }

    public int Revision { get; private set; }

    public TerrainAsset(in TerrainData data) {
        Resolution = data.Resolution;
        Size = data.Size;
        HeightScale = data.HeightScale;
        Heights = (float[])data.Heights.Clone();
    }

    public TerrainData ToData() => new(Resolution, Size, HeightScale, Heights);

    public float Width => Size.X;
    public float Depth => Size.Y;

    public float HeightAt(int x, int z) {
        x = Math.Clamp(x, 0, Resolution - 1);
        z = Math.Clamp(z, 0, Resolution - 1);
        return Heights[z * Resolution + x];
    }

    public void SetHeight(int x, int z, float value) {
        if ((uint)x >= (uint)Resolution || (uint)z >= (uint)Resolution)
            return;
        Heights[z * Resolution + x] = Math.Clamp(value, 0f, 1f);
    }

    public void BumpRevision() => Revision++;

    public void SetDimensions(Vector2 size, float heightScale) {
        Size = size;
        HeightScale = heightScale;
        Revision++;
    }
}
