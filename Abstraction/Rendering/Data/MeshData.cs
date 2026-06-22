
namespace BallisticEngine;

public readonly struct MeshData {
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;

    public readonly Vector4[] Tangents;
    public readonly Vector2[] UVs;
    public readonly uint[] Indices;
    public readonly SubMeshData[] SubMeshes;

    public readonly MeshNodeData[] Nodes;

    public readonly Vector4i[] BoneIndices;
    public readonly Vector4[] BoneWeights;
    public readonly SkeletonData Skeleton;

    /// <summary>
    /// Optional offline signed distance field (generated at import time for Lumen GI). Null for
    /// skinned meshes, when SDF generation is disabled, and for v7-and-earlier artifacts. Existing
    /// code paths are unaffected because every constructor defaults this to null.
    /// </summary>
    public readonly MeshSdf Sdf;

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
        Sdf = null;
    }

    public MeshData(Vector3[] vertices, uint[] indices, Vector2[] uvs, Vector3[] normals, Vector4[] tangents,
        SubMeshData[] subMeshes, MeshNodeData[] nodes,
        Vector4i[] boneIndices, Vector4[] boneWeights, SkeletonData skeleton, MeshSdf sdf = null)
        : this(vertices, indices, uvs, normals, tangents, subMeshes, nodes) {
        BoneIndices = boneIndices;
        BoneWeights = boneWeights;
        Skeleton = skeleton;
        Sdf = sdf;
    }

    /// <summary>Returns a copy carrying the given SDF (all other arrays shared by reference).</summary>
    public MeshData WithSdf(MeshSdf sdf) =>
        new(Vertices, Indices, UVs, Normals, Tangents, SubMeshes, Nodes,
            BoneIndices, BoneWeights, Skeleton, sdf);

    public bool IsValid => Vertices is { Length: > 0 } && Indices is { Length: > 0 };

    public bool IsSkinned =>
        BoneIndices is { Length: > 0 } && BoneWeights is { Length: > 0 } && Skeleton.BoneCount > 0;
}
