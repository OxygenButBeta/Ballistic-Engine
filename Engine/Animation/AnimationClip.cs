using OpenTK.Mathematics;

namespace BallisticEngine;

// A loaded animation clip asset (Unity's AnimationClip). Wraps the CPU keyframe data and samples it
// at a time to produce per-bone LOCAL transforms. Like Mesh/AudioClip it's a BObject, so the asset
// database caches one instance per GUID and scene refs become guid refs automatically.
//
// Sampling is pure CPU and allocation-free into a caller-provided array — the Animator drives it each
// frame, then walks the skeleton (local -> world) and forms skinning matrices. Kept here (Engine
// layer) rather than in Abstraction because it's the runtime kernel the Animator owns.
public sealed class AnimationClip : BObject {
    public AnimationClipData Data { get; }

    public AnimationClip(in AnimationClipData data, string name) {
        Data = data;
        Name = name;
    }

    public float DurationSeconds => Data.DurationSeconds;
    public float DurationTicks => Data.DurationTicks;
    public float TicksPerSecond => Data.TicksPerSecond;

    // Samples the clip at `timeSeconds` into `localPose` (indexed by bone). Bones the clip doesn't
    // animate keep `bindLocal` (the skeleton's default local transform). `localPose` and `bindLocal`
    // must both be length == skeleton.BoneCount. Loops the time into [0, duration) when `loop`.
    public void Sample(float timeSeconds, bool loop, Matrix4[] bindLocal, Matrix4[] localPose) {
        int boneCount = bindLocal.Length;

        // Start every bone at its bind-pose local transform; animated channels overwrite below.
        for (var i = 0; i < boneCount; i++)
            localPose[i] = bindLocal[i];

        float durationSeconds = Data.DurationSeconds;
        float t = timeSeconds;
        if (durationSeconds > 0f) {
            if (loop)
                t = t % durationSeconds;
            else if (t > durationSeconds)
                t = durationSeconds;
            if (t < 0f)
                t += durationSeconds;
        }
        float ticks = t * Data.TicksPerSecond;

        foreach (BoneChannel channel in Data.Channels) {
            if ((uint)channel.BoneIndex >= (uint)boneCount)
                continue;

            Vector3 position = SampleVector(channel.PositionKeys, ticks, ExtractTranslation(bindLocal[channel.BoneIndex]));
            Quaternion rotation = SampleQuaternion(channel.RotationKeys, ticks, ExtractRotation(bindLocal[channel.BoneIndex]));
            Vector3 scale = SampleVector(channel.ScaleKeys, ticks, ExtractScale(bindLocal[channel.BoneIndex]));

            // Row-vector composition (matches Transform.LocalMatrix): Scale * Rotation * Translation.
            localPose[channel.BoneIndex] =
                Matrix4.CreateScale(scale) *
                Matrix4.CreateFromQuaternion(rotation) *
                Matrix4.CreateTranslation(position);
        }
    }

    // Samples the clip at `timeSeconds` into separate per-bone position/rotation/scale arrays (NOT
    // composed to matrices). This is the blendable form the Animator crossfades: two clips sampled to
    // TRS can be lerped (pos/scale) and slerped (rot) per bone, which a matrix lerp can't do correctly.
    // Un-keyed bones get the bind-pose component. All arrays must be length == bindLocal.Length.
    public void SampleLocalTRS(float timeSeconds, bool loop, Matrix4[] bindLocal,
        Vector3[] outPosition, Quaternion[] outRotation, Vector3[] outScale) {
        int boneCount = bindLocal.Length;

        // Default every bone to its bind-pose TRS; animated channels overwrite below.
        for (var i = 0; i < boneCount; i++) {
            outPosition[i] = bindLocal[i].ExtractTranslation();
            outRotation[i] = bindLocal[i].ExtractRotation();
            outScale[i] = bindLocal[i].ExtractScale();
        }

        float durationSeconds = Data.DurationSeconds;
        float t = timeSeconds;
        if (durationSeconds > 0f) {
            if (loop) t %= durationSeconds;
            else if (t > durationSeconds) t = durationSeconds;
            if (t < 0f) t += durationSeconds;
        }
        float ticks = t * Data.TicksPerSecond;

        foreach (BoneChannel channel in Data.Channels) {
            if ((uint)channel.BoneIndex >= (uint)boneCount)
                continue;
            int b = channel.BoneIndex;
            outPosition[b] = SampleVector(channel.PositionKeys, ticks, outPosition[b]);
            outRotation[b] = SampleQuaternion(channel.RotationKeys, ticks, outRotation[b]);
            outScale[b] = SampleVector(channel.ScaleKeys, ticks, outScale[b]);
        }
    }

    // Composes per-bone TRS into local matrices (the inverse of SampleLocalTRS' decomposition).
    public static void ComposeLocal(Vector3[] position, Quaternion[] rotation, Vector3[] scale, Matrix4[] outLocal) {
        for (var i = 0; i < outLocal.Length; i++)
            outLocal[i] =
                Matrix4.CreateScale(scale[i]) *
                Matrix4.CreateFromQuaternion(rotation[i]) *
                Matrix4.CreateTranslation(position[i]);
    }

    // ---- Key interpolation -------------------------------------------------

    static Vector3 SampleVector(VectorKey[] keys, float ticks, Vector3 fallback) {
        if (keys.Length == 0) return fallback;
        if (keys.Length == 1) return keys[0].Value;
        if (ticks <= keys[0].Time) return keys[0].Value;
        if (ticks >= keys[^1].Time) return keys[^1].Value;

        int i = UpperKey(keys.Length, k => keys[k].Time, ticks);
        VectorKey a = keys[i - 1], b = keys[i];
        float span = b.Time - a.Time;
        float f = span > 0f ? (ticks - a.Time) / span : 0f;
        return Vector3.Lerp(a.Value, b.Value, f);
    }

    static Quaternion SampleQuaternion(QuaternionKey[] keys, float ticks, Quaternion fallback) {
        if (keys.Length == 0) return fallback;
        if (keys.Length == 1) return keys[0].Value;
        if (ticks <= keys[0].Time) return keys[0].Value;
        if (ticks >= keys[^1].Time) return keys[^1].Value;

        int i = UpperKey(keys.Length, k => keys[k].Time, ticks);
        QuaternionKey a = keys[i - 1], b = keys[i];
        float span = b.Time - a.Time;
        float f = span > 0f ? (ticks - a.Time) / span : 0f;
        // Slerp, not Lerp — linear quaternion blends twist and shrink, producing visible artifacts.
        return Quaternion.Slerp(a.Value, b.Value, f);
    }

    // First key index whose Time > ticks (the caller already handled the < first / >= last edges,
    // so the result is in [1, length-1]).
    static int UpperKey(int length, System.Func<int, float> timeOf, float ticks) {
        int lo = 1, hi = length - 1;
        while (lo < hi) {
            int mid = (lo + hi) >> 1;
            if (timeOf(mid) <= ticks) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    static Vector3 ExtractTranslation(in Matrix4 m) => m.ExtractTranslation();
    static Quaternion ExtractRotation(in Matrix4 m) => m.ExtractRotation();
    static Vector3 ExtractScale(in Matrix4 m) => m.ExtractScale();
}
