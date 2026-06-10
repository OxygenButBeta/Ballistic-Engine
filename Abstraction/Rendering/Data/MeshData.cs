using OpenTK.Mathematics;

namespace BallisticEngine;

// CPU-side mesh geometry. Carries no GPU state, so it can be produced off the GL thread.
// All arrays are expected non-null and vertex-count aligned; importers fill defaults.
public readonly struct MeshData {
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Vector3[] Tangents;
    public readonly Vector2[] UVs;
    public readonly uint[] Indices;

    public MeshData(Vector3[] vertices, uint[] indices, Vector2[] uvs, Vector3[] normals, Vector3[] tangents) {
        Vertices = vertices;
        Indices = indices;
        UVs = uvs;
        Normals = normals;
        Tangents = tangents;
    }

    public bool IsValid => Vertices is { Length: > 0 } && Indices is { Length: > 0 };
}
