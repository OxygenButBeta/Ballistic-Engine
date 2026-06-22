
namespace BallisticEngine;

public static class TerrainMeshBuilder {
    public static MeshData Build(in TerrainData data) {
        if (!data.IsValid)
            throw new ArgumentException("TerrainData is invalid (resolution/size/heights).");

        int res = data.Resolution;
        int vertexCount = res * res;
        float halfX = data.Size.X * 0.5f;
        float halfZ = data.Size.Y * 0.5f;
        float stepX = data.Size.X / (res - 1);
        float stepZ = data.Size.Y / (res - 1);
        float heightScale = data.HeightScale;
        float[] heights = data.Heights;

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var tangents = new Vector4[vertexCount];
        var uvs = new Vector2[vertexCount];

        for (int z = 0; z < res; z++) {
            for (int x = 0; x < res; x++) {
                int i = z * res + x;
                float y = heights[i] * heightScale;

                vertices[i] = new Vector3(-halfX + x * stepX, y, -halfZ + z * stepZ);
                uvs[i] = new Vector2(x / (float)(res - 1), z / (float)(res - 1));
                normals[i] = NormalAt(heights, res, x, z, stepX, stepZ, heightScale);
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
            }
        }

        int cells = (res - 1) * (res - 1);
        var indices = new uint[cells * 6];
        int t = 0;
        for (int z = 0; z < res - 1; z++) {
            for (int x = 0; x < res - 1; x++) {
                uint topLeft = (uint)(z * res + x);
                uint topRight = topLeft + 1;
                uint bottomLeft = (uint)((z + 1) * res + x);
                uint bottomRight = bottomLeft + 1;

                indices[t++] = topLeft;
                indices[t++] = bottomLeft;
                indices[t++] = topRight;

                indices[t++] = topRight;
                indices[t++] = bottomLeft;
                indices[t++] = bottomRight;
            }
        }

        return new MeshData(vertices, indices, uvs, normals, tangents);
    }

    static Vector3 NormalAt(float[] heights, int res, int x, int z, float stepX, float stepZ, float heightScale) {
        float hl = Sample(heights, res, x - 1, z) * heightScale;
        float hr = Sample(heights, res, x + 1, z) * heightScale;
        float hd = Sample(heights, res, x, z - 1) * heightScale;
        float hu = Sample(heights, res, x, z + 1) * heightScale;

        float dx = (hr - hl) / (2f * stepX);
        float dz = (hu - hd) / (2f * stepZ);

        return new Vector3(-dx, 1f, -dz).Normalized();
    }

    static float Sample(float[] heights, int res, int x, int z) {
        x = Math.Clamp(x, 0, res - 1);
        z = Math.Clamp(z, 0, res - 1);
        return heights[z * res + x];
    }
}
