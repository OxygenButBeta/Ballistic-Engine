using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Assimp;
using Assimp.Configs;

namespace BallisticEngine;

public class Mesh : BObject {
    public readonly Vector3[] Vertices;
    public readonly uint[] Indices;
    public readonly Vector3[] Normals;
    public readonly Vector2[] UVs;
    public readonly Vector4[] Tangents;

    Mesh(in Vector3[] vertices, in uint[] indices, Vector3[] normals, Vector2[] uvs, Vector4[] tangents) {
        Vertices = vertices;
        Indices = indices;
        Normals = normals;
        Tangents = tangents;
        UVs = uvs;
        UploadToGPU();
    }

   public static Mesh ImportFromFile(string filePath) {
    var context = new AssimpContext();

   Assimp.Scene scene = context.ImportFile(filePath,
        PostProcessSteps.Triangulate | PostProcessSteps.CalculateTangentSpace);

    if (scene == null || scene.MeshCount == 0)
        throw new Exception("Model yüklenemedi veya mesh bulunamadı.");

    var mesh = scene.Meshes[0];

    // Vertexler
    Vector3[] vertices = new Vector3[mesh.VertexCount];
    for (int i = 0; i < mesh.VertexCount; i++) {
        var v = mesh.Vertices[i];
        vertices[i] = new Vector3(v.X, v.Y, v.Z);
    }

    // Sadece üçgen olanların sayısını al
    int triangleCount = mesh.Faces.Count(f => f.IndexCount == 3);
    uint[] indices = new uint[triangleCount * 3];

    int index = 0;
    foreach (var face in mesh.Faces) {
        if (face.IndexCount != 3)
            continue;

        indices[index++] = (uint)face.Indices[0];
        indices[index++] = (uint)face.Indices[1];
        indices[index++] = (uint)face.Indices[2];
    }

    Vector3[] normals = new Vector3[mesh.VertexCount];
    if (mesh.HasNormals) {
        for (int i = 0; i < mesh.VertexCount; i++) {
            var n = mesh.Normals[i];
            normals[i] = new Vector3(n.X, n.Y, n.Z);
        }
    }
    else {
        for (int i = 0; i < mesh.VertexCount; i++)
            normals[i] = Vector3.Zero;
    }

    Vector2[] uvs = new Vector2[mesh.VertexCount];
    if (mesh.HasTextureCoords(0)) {
        for (int i = 0; i < mesh.VertexCount; i++) {
            var uv = mesh.TextureCoordinateChannels[0][i];
            uvs[i] = new Vector2(uv.X, uv.Y);
        }
    }
    else {
        for (int i = 0; i < mesh.VertexCount; i++)
            uvs[i] = Vector2.Zero;
    }

    Vector4[] tangents = new Vector4[mesh.VertexCount];
    if (mesh.HasTangentBasis) {
        for (int i = 0; i < mesh.VertexCount; i++) {
            var t = mesh.Tangents[i];
            var b = mesh.BiTangents[i];
            Vector3 n = normals[i];
            Vector3 tan = new Vector3(t.X, t.Y, t.Z);
            Vector3 bitan = new Vector3(b.X, b.Y, b.Z);
            float w = Vector3.Dot(Vector3.Cross(n, tan), bitan) < 0 ? -1f : 1f;
            tangents[i] = new Vector4(tan.X, tan.Y, tan.Z, w);
        }
    }
    else {
        for (int i = 0; i < mesh.VertexCount; i++)
            tangents[i] = new Vector4(1, 0, 0, 1);
    }

    return new Mesh(vertices, indices, normals, uvs, tangents);
}

    public int VAO { get; private set; }
    public int VBO { get; private set; }
    public int EBO { get; private set; }

    public Mesh[] SubMeshes { get; private set; }
    public bool IsUploaded { get; private set; }

    public void UploadToGPU() {
        VAO = GL.GenVertexArray();
        VBO = GL.GenBuffer();
        EBO = GL.GenBuffer();

        int vertexCount = Vertices.Length;
        int floatsPerVertex = 3 + 3 + 2 + 4; // Position + Normal + UV + Tangent

        float[] interleavedData = new float[vertexCount * floatsPerVertex];

        for (int i = 0; i < vertexCount; i++) {
            int offset = i * floatsPerVertex;

            interleavedData[offset + 0] = Vertices[i].X;
            interleavedData[offset + 1] = Vertices[i].Y;
            interleavedData[offset + 2] = Vertices[i].Z;

            interleavedData[offset + 3] = Normals[i].X;
            interleavedData[offset + 4] = Normals[i].Y;
            interleavedData[offset + 5] = Normals[i].Z;

            interleavedData[offset + 6] = UVs[i].X;
            interleavedData[offset + 7] = UVs[i].Y;

            interleavedData[offset + 8] = Tangents[i].X;
            interleavedData[offset + 9] = Tangents[i].Y;
            interleavedData[offset + 10] = Tangents[i].Z;
            interleavedData[offset + 11] = Tangents[i].W;
        }

        GL.BindVertexArray(VAO);

        // Vertex Buffer
        GL.BindBuffer(BufferTarget.ArrayBuffer, VBO);
        GL.BufferData(BufferTarget.ArrayBuffer, interleavedData.Length * sizeof(float), interleavedData,
            BufferUsageHint.StaticDraw);

        // Element Buffer
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, EBO);
        GL.BufferData(BufferTarget.ElementArrayBuffer, Indices.Length * sizeof(uint), Indices,
            BufferUsageHint.StaticDraw);

        int stride = floatsPerVertex * sizeof(float);

        // Position (location = 0)
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);

        // Normal (location = 1)
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        // UV (location = 2)
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        // Tangent (location = 3)
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(3);

        GL.BindVertexArray(0);

        IsUploaded = true;
    }
}