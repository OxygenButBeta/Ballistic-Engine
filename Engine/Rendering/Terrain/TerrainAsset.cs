using OpenTK.Mathematics;

namespace BallisticEngine;

// A loaded .terrain asset: the editable height field plus its grid dimensions. This is the BObject
// the Terrain component references (serialized as a guid ref) and the sculpt tools mutate. It owns
// no GPU state — the Terrain component turns it into a Mesh via TerrainMeshBuilder.
//
// Heights are a row-major Resolution x Resolution grid in [0,1]; world height = Heights[i] *
// HeightScale, and the grid spans Size (world XZ) centered on the entity origin (matching
// TerrainMeshBuilder). Mutations bump Revision so the component can detect edits and rebuild.
public sealed class TerrainAsset : BObject {
    public int Resolution { get; }
    public Vector2 Size { get; private set; }
    public float HeightScale { get; private set; }
    public float[] Heights { get; }

    // Incremented on every in-place edit (sculpt stroke, size change). The Terrain component caches
    // the last value it built from and rebuilds the mesh when it changes.
    public int Revision { get; private set; }

    public TerrainAsset(in TerrainData data) {
        Resolution = data.Resolution;
        Size = data.Size;
        HeightScale = data.HeightScale;
        // Copy so the component/tools own a private, mutable field independent of the decode buffer.
        Heights = (float[])data.Heights.Clone();
    }

    public TerrainData ToData() => new(Resolution, Size, HeightScale, Heights);

    public float Width => Size.X;
    public float Depth => Size.Y;

    // Sample the normalized height at grid coords, clamped to the field.
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

    // Call after a batch of SetHeight/Heights edits so the component rebuilds its mesh.
    public void BumpRevision() => Revision++;

    public void SetDimensions(Vector2 size, float heightScale) {
        Size = size;
        HeightScale = heightScale;
        Revision++;
    }
}
