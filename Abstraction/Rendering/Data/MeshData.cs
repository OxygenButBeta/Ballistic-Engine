
namespace BallisticEngine;

// CPU-side mesh geometry. Carries no GPU state, so it can be produced off the GL thread.
// All arrays are expected non-null and vertex-count aligned; importers fill defaults.
// SubMeshes partition the index buffer by material; a mesh always has at least one.
public readonly struct MeshData {
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    // xyz = tangent, w = bitangent handedness (+1/-1). Mirrored UV islands carry w = -1 so
    // the shader reconstructs B = cross(N, T) * w instead of shading inverted bumps.
    public readonly Vector4[] Tangents;
    public readonly Vector2[] UVs;
    public readonly uint[] Indices;
    public readonly SubMeshData[] SubMeshes;

    // The source model's node hierarchy (pre-order; see MeshNodeData) — lets the editor
    // instantiate the model as a matching entity tree. Empty unless imported split-by-nodes.
    public readonly MeshNodeData[] Nodes;

    // ---- Skinning (null for static meshes) ----------------------------------
    // Per-vertex bone influences: up to 4 bones, weights summing to 1. BoneIndices index into
    // Skeleton.BoneNames. Both null (and Skeleton.BoneCount == 0) for an un-skinned mesh — the
    // renderer's static path is unaffected.
    public readonly Vector4i[] BoneIndices;   // 4 bone indices per vertex (-1/0-padded)
    public readonly Vector4[] BoneWeights;    // 4 weights per vertex, sum == 1
    public readonly SkeletonData Skeleton;    // the bone hierarchy these indices reference

    public MeshData(Vector3[] vertices, uint[] indices, Vector2[] uvs, Vector3[] normals, Vector4[] tangents)
        : this(vertices, indices, uvs, normals, tangents,
            [new SubMeshData(null, 0, indices?.Length ?? 0, null)]) {
    }

    public MeshData(Vector3[] vertices, uint[] indices, Vector2[] uvs, Vector3[] normals, Vector4[] tangents,
        SubMeshData[] subMeshes, MeshNodeData[] nodes = null) {
        Vertices = vertices;
        Indices = indices;
        UVs = uvs;
        Normals = normals;
        Tangents = tangents;
        SubMeshes = subMeshes is { Length: > 0 }
            ? subMeshes
            : [new SubMeshData(null, 0, indices?.Length ?? 0, null)];
        Nodes = nodes ?? [];
        BoneIndices = null;
        BoneWeights = null;
        Skeleton = default;
    }

    // Skinned-mesh ctor: same geometry plus per-vertex bone influences and the skeleton.
    public MeshData(Vector3[] vertices, uint[] indices, Vector2[] uvs, Vector3[] normals, Vector4[] tangents,
        SubMeshData[] subMeshes, MeshNodeData[] nodes,
        Vector4i[] boneIndices, Vector4[] boneWeights, SkeletonData skeleton)
        : this(vertices, indices, uvs, normals, tangents, subMeshes, nodes) {
        BoneIndices = boneIndices;
        BoneWeights = boneWeights;
        Skeleton = skeleton;
    }

    public bool IsValid => Vertices is { Length: > 0 } && Indices is { Length: > 0 };

    // True when this mesh carries usable skinning data (every skinned-path consumer gates on this).
    public bool IsSkinned =>
        BoneIndices is { Length: > 0 } && BoneWeights is { Length: > 0 } && Skeleton.BoneCount > 0;
}
