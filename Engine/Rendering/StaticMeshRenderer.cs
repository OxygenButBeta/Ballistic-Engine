using OpenTK.Mathematics;

namespace BallisticEngine;

public class StaticMeshRenderer : Renderer {
    public override Mesh Mesh { get; protected set; }
    public override Material Material { get; protected set; }

    protected internal override void OnBegin() {
        Vector3[] cubeVertices = {
            new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)
        };
        int[] cubeIndices = {
            0, 1, 2, 2, 3, 0,
            4, 5, 6, 6, 7, 4,
            0, 1, 5, 5, 4, 0,
            2, 3, 7, 7, 6, 2,
            0, 3, 7, 7, 4, 0,
            1, 2, 6, 6, 5, 1
        };
        Vector3[] normals = {
            new Vector3(-1, -1, -1).Normalized(),
            new Vector3(1, -1, -1).Normalized(),
            new Vector3(1, 1, -1).Normalized(),
            new Vector3(-1, 1, -1).Normalized(),
            new Vector3(-1, -1, 1).Normalized(),
            new Vector3( 1, -1, 1).Normalized(),
            new Vector3(1, 1, 1).Normalized(),
            new Vector3(-1, 1, 1).Normalized(),
        };
        Vector2[] uvs = {
            new(0, 0),
            new(1, 0),
            new(1, 1),
            new(0, 1),
            new(0, 0),
            new(1, 0),
            new(1, 1),
            new(0, 1),
        };
        Mesh = new Mesh(cubeVertices, cubeIndices, normals, uvs);
        Texture2D defaultTexture = new();
        Shader defaultShader = new();
        Material = new Material(defaultTexture, defaultShader);
    }

    protected internal override void OnEnabled() {
        RenderTargets.Add(this);
    }

    protected internal override void OnDisabled() {
        RenderTargets.Remove(this);
    }

}