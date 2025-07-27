using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace BallisticEngine;

public class Mesh : BObject {
    public readonly Vector3[] Vertices;
    public readonly int[] Indices;
    public readonly Vector3[] Normals;
    public readonly Vector2[] UVs;
    public readonly Vector4[] Tangents;

    public Mesh(in Vector3[] vertices, in int[] indices) {
        Vertices = vertices;
        Indices = indices;
        UploadToGPU();
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

        GL.BindVertexArray(VAO);

        // Vertex buffer
        GL.BindBuffer(BufferTarget.ArrayBuffer, VBO);
        GL.BufferData(BufferTarget.ArrayBuffer, Vertices.Length * Vector3.SizeInBytes, Vertices,
            BufferUsageHint.StaticDraw);

        // Element buffer
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, EBO);
        GL.BufferData(BufferTarget.ElementArrayBuffer, Indices.Length * sizeof(int), Indices,
            BufferUsageHint.StaticDraw);

        // Position (location = 0)
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);

        IsUploaded = true;
    }
}