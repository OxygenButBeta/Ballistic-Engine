
namespace BallisticEngine;

// CPU-side skeleton for a skinned mesh — the bone hierarchy plus the matrices that bind it to the
// mesh's vertices. Pure data (Abstraction layer: BCL + OpenTK.Mathematics only), so the importer
// produces it and the engine consumes it without either touching the GL backend or Assimp.
//
// Bones are stored in PRE-ORDER: ParentIndices[i] always refers to an earlier entry (-1 for a root),
// so a single forward pass computes world matrices (worldBone[i] = local[i] * worldBone[parent]).
// This is the same convention MeshNodeData uses for the node hierarchy.
public readonly struct SkeletonData {
    public readonly string[] BoneNames;            // bone i's name (matches a source node name)
    public readonly int[] ParentIndices;           // parent bone index, -1 for a root (parent < i)
    public readonly Matrix4[] InverseBindPose;      // mesh-space -> bone-space at bind (Assimp offset matrix)
    public readonly Matrix4[] BindPoseLocal;        // bone's default local transform (used when un-animated)

    public SkeletonData(string[] boneNames, int[] parentIndices,
        Matrix4[] inverseBindPose, Matrix4[] bindPoseLocal) {
        BoneNames = boneNames ?? System.Array.Empty<string>();
        ParentIndices = parentIndices ?? System.Array.Empty<int>();
        InverseBindPose = inverseBindPose ?? System.Array.Empty<Matrix4>();
        BindPoseLocal = bindPoseLocal ?? System.Array.Empty<Matrix4>();
    }

    public int BoneCount => BoneNames?.Length ?? 0;
    public bool IsValid => BoneCount > 0
        && ParentIndices.Length == BoneCount
        && InverseBindPose.Length == BoneCount
        && BindPoseLocal.Length == BoneCount;

    // Finds a bone by name (animation channels are keyed by node name). -1 when absent.
    public int IndexOf(string boneName) {
        if (BoneNames is null)
            return -1;
        for (var i = 0; i < BoneNames.Length; i++)
            if (BoneNames[i] == boneName)
                return i;
        return -1;
    }
}
