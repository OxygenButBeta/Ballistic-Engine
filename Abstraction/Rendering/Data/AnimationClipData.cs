
namespace BallisticEngine;

public readonly struct AnimationClipData {
    public readonly string Name;
    public readonly float DurationTicks;
    public readonly float TicksPerSecond;
    public readonly BoneChannel[] Channels;

    public AnimationClipData(string name, float durationTicks, float ticksPerSecond, BoneChannel[] channels) {
        Name = name ?? "";
        DurationTicks = durationTicks;
        TicksPerSecond = ticksPerSecond > 0f ? ticksPerSecond : 25f;
        Channels = channels ?? System.Array.Empty<BoneChannel>();
    }

    public float DurationSeconds => TicksPerSecond > 0f ? DurationTicks / TicksPerSecond : 0f;
    public bool IsValid => Channels is { Length: > 0 } && DurationTicks > 0f;
}

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

public readonly struct VectorKey {
    public readonly float Time;
    public readonly Vector3 Value;
    public VectorKey(float time, Vector3 value) { Time = time; Value = value; }
}

public readonly struct QuaternionKey {
    public readonly float Time;
    public readonly Quaternion Value;
    public QuaternionKey(float time, Quaternion value) { Time = time; Value = value; }
}
