
namespace BallisticEngine;

public readonly struct SkeletonData {
    public readonly string[] BoneNames;
    public readonly int[] ParentIndices;
    public readonly Matrix4[] InverseBindPose;
    public readonly Matrix4[] BindPoseLocal;

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

    public int IndexOf(string boneName) {
        if (BoneNames is null)
            return -1;
        for (var i = 0; i < BoneNames.Length; i++)
            if (BoneNames[i] == boneName)
                return i;
        return -1;
    }
}
