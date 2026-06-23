
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

    /// <summary>
    /// Optional offline mesh-card representation (Lumen FAZ 3a; built from <see cref="Sdf"/>). Null for
    /// skinned meshes, when card generation is disabled, when the SDF is absent, and for v8-and-earlier
    /// artifacts. Every constructor defaults this to null, so existing code paths are unaffected.
    /// </summary>
    public readonly MeshCards Cards;

    /// <summary>
    /// Optional PER-SUBMESH card representation (Lumen FAZ 8.6), parallel to <see cref="SubMeshes"/>:
    /// <c>SubMeshCards[i]</c> is the card set for <c>SubMeshes[i]</c>, built in that submesh's LOCAL space
    /// (mesh-local transformed by inverse(NodeTransform) — the same space MeshCollider uses). Used for
    /// whole-mesh-merge / split-by-nodes meshes (Bistro) where ONE coarse whole-mesh SDF can't place
    /// cards: each component gets its own tight SDF+cards at import, and the runtime places each
    /// submesh's cards via instanceWorld * NodeTransform. Null (and entries may be null) for single-submesh
    /// meshes (CornellBox stays on the whole-mesh <see cref="Cards"/> path), skinned meshes, when disabled,
    /// and for v9-and-earlier artifacts. Every constructor defaults this to null, so existing paths are
    /// unaffected. NOTE: only the per-submesh CARDS are stored (small); the per-submesh SDFs they were
    /// built from are discarded — the runtime only needs cards.
    /// </summary>
    public readonly MeshCards[] SubMeshCards;

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
        Cards = null;
        SubMeshCards = null;
    }

    public MeshData(Vector3[] vertices, uint[] indices, Vector2[] uvs, Vector3[] normals, Vector4[] tangents,
        SubMeshData[] subMeshes, MeshNodeData[] nodes,
        Vector4i[] boneIndices, Vector4[] boneWeights, SkeletonData skeleton, MeshSdf sdf = null,
        MeshCards cards = null, MeshCards[] subMeshCards = null)
        : this(vertices, indices, uvs, normals, tangents, subMeshes, nodes) {
        BoneIndices = boneIndices;
        BoneWeights = boneWeights;
        Skeleton = skeleton;
        Sdf = sdf;
        Cards = cards;
        SubMeshCards = subMeshCards;
    }

    /// <summary>Returns a copy carrying the given SDF (all other arrays/cards shared by reference).</summary>
    public MeshData WithSdf(MeshSdf sdf) =>
        new(Vertices, Indices, UVs, Normals, Tangents, SubMeshes, Nodes,
            BoneIndices, BoneWeights, Skeleton, sdf, Cards, SubMeshCards);

    /// <summary>Returns a copy carrying the given mesh cards (all other arrays/SDF shared by reference).</summary>
    public MeshData WithCards(MeshCards cards) =>
        new(Vertices, Indices, UVs, Normals, Tangents, SubMeshes, Nodes,
            BoneIndices, BoneWeights, Skeleton, Sdf, cards, SubMeshCards);

    /// <summary>Returns a copy carrying the given per-submesh cards (all other arrays/SDF/cards shared).</summary>
    public MeshData WithSubMeshCards(MeshCards[] subMeshCards) =>
        new(Vertices, Indices, UVs, Normals, Tangents, SubMeshes, Nodes,
            BoneIndices, BoneWeights, Skeleton, Sdf, Cards, subMeshCards);

    public bool IsValid => Vertices is { Length: > 0 } && Indices is { Length: > 0 };

    public bool IsSkinned =>
        BoneIndices is { Length: > 0 } && BoneWeights is { Length: > 0 } && Skeleton.BoneCount > 0;
}
