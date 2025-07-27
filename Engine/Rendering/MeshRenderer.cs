using OpenTK.Mathematics;

namespace BallisticEngine;

public class MeshRenderer : Behaviour, IRenderTarget {
    public static List<IRenderTarget> RenderTargets { get; } = new();
    public Mesh Mesh { get; private set; }
    public Material Material { get; private set; }



    protected internal override void OnBegin() {
        Vector3[] cubeVertices = {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1)
        };
        int[] cubeIndices = {
            0, 1, 2, 2, 3, 0, // Back face
            4, 5, 6, 6, 7, 4, // Front face
            0, 1, 5, 5, 4, 0, // Bottom face
            2, 3, 7, 7, 6, 2, // Top face
            0, 3, 7, 7, 4, 0, // Left face
            1, 2, 6, 6, 5, 1 // Right face
        };
        Mesh = new Mesh(cubeVertices, cubeIndices);
        Texture2D defaultTexture = new();
        Shader defaultShader = new();
        Material = new Material(defaultTexture, defaultShader);
    }

    protected internal override void OnEnabled() {
        Console.WriteLine( "MeshRenderer OnEnabled called");
        RenderTargets.Add(this);
    }

    protected internal override void OnDisabled() {
        RenderTargets.Remove(this);
    }
}