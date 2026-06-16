
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

    // Returns a copy of this clip whose channels are REMAPPED onto `targetSkeleton` by bone NAME — the runtime
    // retarget that makes a Mixamo animation FBX play on a separately-imported character with the same rig but a
    // different bone import order. Channels whose name has no match in the target are dropped. Requires the clip
    // to carry bone names (v2+ .banim); a nameless clip (v1) is returned unchanged (assumed same-order). Pure;
    // the caller caches the result (one retarget per clip+skeleton pair).
    public AnimationClip RetargetTo(SkeletonData targetSkeleton) {
        BoneChannel[] src = Data.Channels;
        if (src is null || src.Length == 0) return this;

        // If no channel carries a name, this is a v1 clip — nothing to remap by; play as-is (same-order).
        bool hasNames = false;
        for (int i = 0; i < src.Length; i++)
            if (!string.IsNullOrEmpty(src[i].BoneName)) { hasNames = true; break; }
        if (!hasNames) return this;

        var remapped = new List<BoneChannel>(src.Length);
        for (int i = 0; i < src.Length; i++) {
            BoneChannel c = src[i];
            int targetIndex = targetSkeleton.IndexOf(c.BoneName);
            if (targetIndex < 0) continue;   // a bone the target rig doesn't have — drop the channel
            remapped.Add(new BoneChannel(targetIndex, c.BoneName, c.PositionKeys, c.RotationKeys, c.ScaleKeys));
        }

        var data = new AnimationClipData(Data.Name, Data.DurationTicks, Data.TicksPerSecond, remapped.ToArray());
        return new AnimationClip(data, Name);
    }

    // True when the clip already matches `targetSkeleton` (every named channel's index equals the target's index
    // for that name) — so RetargetTo would be a no-op and the caller can skip caching a copy.
    public bool MatchesSkeleton(SkeletonData targetSkeleton) {
        BoneChannel[] src = Data.Channels;
        if (src is null) return true;
        for (int i = 0; i < src.Length; i++) {
            BoneChannel c = src[i];
            if (string.IsNullOrEmpty(c.BoneName)) continue;
            if (targetSkeleton.IndexOf(c.BoneName) != c.BoneIndex) return false;
        }
        return true;
    }

    // Samples ONE bone's local position + rotation at `timeSeconds` (root-motion extraction needs only the
    // root channel, so sampling the whole skeleton would be wasteful). Falls back to `bindPos`/`bindRot` for
    // an un-keyed component. Time is looped into [0, duration) when `loop`.
    public void SampleBoneLocal(float timeSeconds, int boneIndex, bool loop,
        Vector3 bindPos, Quaternion bindRot, out Vector3 position, out Quaternion rotation) {
        position = bindPos;
        rotation = bindRot;

        float durationSeconds = Data.DurationSeconds;
        float t = timeSeconds;
        if (durationSeconds > 0f) {
            if (loop) t %= durationSeconds;
            else if (t > durationSeconds) t = durationSeconds;
            if (t < 0f) t += durationSeconds;
        }
        float ticks = t * Data.TicksPerSecond;

        foreach (BoneChannel channel in Data.Channels) {
            if (channel.BoneIndex != boneIndex) continue;
            position = SampleVector(channel.PositionKeys, ticks, bindPos);
            rotation = SampleQuaternion(channel.RotationKeys, ticks, bindRot);
            return;
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
