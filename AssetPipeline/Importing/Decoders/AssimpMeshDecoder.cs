using Assimp;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// The only place in the engine that talks to Assimp.
public static class AssimpMeshDecoder {
    public static MeshData Decode(string path, bool flipUVs = true, int meshIndex = 0) {
        AssimpContext context = new();

        PostProcessSteps steps = PostProcessSteps.Triangulate;
        if (flipUVs)
            steps |= PostProcessSteps.FlipUVs;

        Assimp.Scene scene = context.ImportFile(path, steps);

        if (scene == null || scene.MeshCount == 0)
            throw new IOException($"Mesh import failed or no meshes found in '{path}'.");

        if (meshIndex < 0 || meshIndex >= scene.MeshCount)
            throw new IOException(
                $"Mesh index {meshIndex} is out of range; '{path}' contains {scene.MeshCount} meshes.");

        Assimp.Mesh mesh = scene.Meshes[meshIndex];

        Vector3[] vertices = new Vector3[mesh.VertexCount];
        for (var i = 0; i < mesh.VertexCount; i++) {
            Vector3D v = mesh.Vertices[i];
            vertices[i] = new Vector3(v.X, v.Y, v.Z);
        }

        Vector2[] uvs = new Vector2[mesh.VertexCount];
        if (mesh.TextureCoordinateChannelCount > 0 && mesh.HasTextureCoords(0)) {
            for (var i = 0; i < mesh.VertexCount; i++) {
                Vector3D uv = mesh.TextureCoordinateChannels[0][i];
                uvs[i] = new Vector2(uv.X, uv.Y);
            }
        }

        var triangleCount = mesh.Faces.Count(f => f.IndexCount == 3);
        var indices = new uint[triangleCount * 3];

        var index = 0;
        foreach (Face face in mesh.Faces.Where(face => face.IndexCount == 3)) {
            indices[index++] = (uint)face.Indices[0];
            indices[index++] = (uint)face.Indices[1];
            indices[index++] = (uint)face.Indices[2];
        }

        Vector3[] normals = new Vector3[mesh.VertexCount];
        if (mesh.HasNormals) {
            for (var i = 0; i < mesh.VertexCount; i++) {
                Vector3D n = mesh.Normals[i];
                normals[i] = new Vector3(n.X, n.Y, n.Z);
            }
        }

        Vector3[] tangents = new Vector3[mesh.VertexCount];
        if (mesh.HasTangentBasis) {
            for (var i = 0; i < mesh.VertexCount; i++) {
                Vector3D t = mesh.Tangents[i];
                tangents[i] = new Vector3(t.X, t.Y, t.Z);
            }
        }
        else {
            for (var i = 0; i < mesh.VertexCount; i++)
                tangents[i] = Vector3.UnitX;
        }

        return new MeshData(vertices, indices, uvs, normals, tangents);
    }
}
