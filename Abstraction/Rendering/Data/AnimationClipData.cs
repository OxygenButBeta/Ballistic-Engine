
namespace BallisticEngine;

// CPU-side keyframed animation (Abstraction layer: BCL + OpenTK.Mathematics only). One clip is a set
// of per-bone channels; each channel holds separate position/rotation/scale key tracks (the Assimp /
// glTF model). The engine's AnimationClip samples this at a time to produce per-bone local TRS.
//
// Times are in TICKS; seconds = ticks / TicksPerSecond. Assimp reports both per clip; un-set
// TicksPerSecond falls back to 25 (the Assimp default) so a clip is never zero-rate.
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

// One bone's animation: its index into the skeleton (resolved at import from the channel's node
// name) and three independently-keyed tracks. A track may be empty (the bone doesn't animate that
// component) — the sampler then uses the bind-pose value for that component.
public readonly struct BoneChannel {
    public readonly int BoneIndex;
    // The source bone's NAME, kept so a clip can be RETARGETED onto a different skeleton (e.g. a Mixamo
    // animation FBX played on a separately-imported character) by matching names instead of the fragile
    // import-order index. Empty for v1 .banim artifacts (pre-retarget) — those only work on the same order.
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

// Keyframes are blittable structs so the artifact reads/writes them with one MemoryMarshal blit.
public readonly struct VectorKey {
    public readonly float Time;     // in ticks
    public readonly Vector3 Value;
    public VectorKey(float time, Vector3 value) { Time = time; Value = value; }
}

public readonly struct QuaternionKey {
    public readonly float Time;     // in ticks
    public readonly Quaternion Value;
    public QuaternionKey(float time, Quaternion value) { Time = time; Value = value; }
}
