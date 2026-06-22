
namespace BallisticEngine;

public readonly struct BoneChannel {
    public readonly int BoneIndex;

    public readonly string BoneName;
    public readonly VectorKey[] PositionKeys;
    public readonly QuaternionKey[] RotationKeys;
    public readonly VectorKey[] ScaleKeys;

    public BoneChannel(int boneIndex, VectorKey[] positionKeys, QuaternionKey[] rotationKeys, VectorKey[] scaleKeys)
        : this(boneIndex, null, positionKeys, rotationKeys, scaleKeys) { }

    public BoneChannel(int boneIndex, string boneName, VectorKey[] positionKeys, QuaternionKey[] rotationKeys, VectorKey[] scaleKeys) {
        BoneIndex = boneIndex;
        BoneName = boneName ?? "";
        PositionKeys = positionKeys ?? System.Array.Empty<VectorKey>();
        RotationKeys = rotationKeys ?? System.Array.Empty<QuaternionKey>();
        ScaleKeys = scaleKeys ?? System.Array.Empty<VectorKey>();
    }
}
