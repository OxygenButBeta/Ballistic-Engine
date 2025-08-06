using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Assimp;
using Assimp.Configs;
using BufferUsageHint = OpenTK.Graphics.OpenGL4.BufferUsageHint;

namespace BallisticEngine;

public class Mesh : BObject {
    public readonly Vector3[] Vertices;
    public readonly uint[] Indices;
    public readonly Vector2[] UVs;

    readonly VertexArrayObject VertexArrayObject;
    readonly VertexBuffer3D vertexBuffer;
    readonly IndexBuffer indexBuffer;
    readonly UVBuffer2D uvBuffer;

    Mesh(in Vector3[] vertices, in uint[] indices, Vector2[] uVs) {
        Vertices = vertices;
        Indices = indices;
        VertexArrayObject = new VertexArrayObject();
        vertexBuffer = new VertexBuffer3D(VertexArrayObject);
        indexBuffer = new IndexBuffer(VertexArrayObject);
        uvBuffer = new UVBuffer2D(VertexArrayObject);
        UVs = uVs;
        UploadToGPU();
    }

    public static Mesh ImportFromFile(string filePath) {
        var context = new AssimpContext();

        Assimp.Scene scene = context.ImportFile(filePath,
            PostProcessSteps.Triangulate  | PostProcessSteps.FlipUVs);

        if (scene == null || scene.MeshCount == 0)
            throw new Exception("Model yüklenemedi veya mesh bulunamadı.");

        var mesh = scene.Meshes[0];

        // Vertexler
        Vector3[] vertices = new Vector3[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++) {
            var v = mesh.Vertices[i];
            vertices[i] = new Vector3(v.X, v.Y, v.Z);
        }

        Vector2[] uvs = new Vector2[mesh.VertexCount];
        if (mesh.TextureCoordinateChannelCount > 0 && mesh.HasTextureCoords(0)) {
            for (int i = 0; i < mesh.VertexCount; i++) {
                var uv = mesh.TextureCoordinateChannels[0][i];
                uvs[i] = new Vector2(uv.X, uv.Y);
            }
        }
        else {
            // Eğer UV yoksa 0 olarak doldur
            for (int i = 0; i < mesh.VertexCount; i++) {
                uvs[i] = Vector2.Zero;
            }
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

        return new Mesh(vertices, indices, uvs);
    }

    public bool IsUploaded { get; private set; }

    public void Bind() {
        VertexArrayObject.Bind();

    }

    public void UploadToGPU() {
        uvBuffer.CreateBuffer();
        uvBuffer.SetData(UVs, BufferUsageHint.StaticDraw);

        vertexBuffer.CreateBuffer();
        vertexBuffer.SetData(Vertices, BufferUsageHint.StaticDraw);

        indexBuffer.CreateBuffer();
        indexBuffer.SetData(Indices, BufferUsageHint.StaticDraw);
        IsUploaded = true;
    }
}