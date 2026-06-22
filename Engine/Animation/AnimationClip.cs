
namespace BallisticEngine;

public sealed class AnimationClip : BObject {
    public AnimationClipData Data { get; }

    public AnimationClip(in AnimationClipData data, string name) {
        Data = data;
        Name = name;
    }

    public float DurationSeconds => Data.DurationSeconds;
    public float DurationTicks => Data.DurationTicks;
    public float TicksPerSecond => Data.TicksPerSecond;

    public void Sample(float timeSeconds, bool loop, Matrix4[] bindLocal, Matrix4[] localPose) {
        int boneCount = bindLocal.Length;

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

            localPose[channel.BoneIndex] =
                Matrix4.CreateScale(scale) *
                Matrix4.CreateFromQuaternion(rotation) *
                Matrix4.CreateTranslation(position);
        }
    }

    public void SampleLocalTRS(float timeSeconds, bool loop, Matrix4[] bindLocal,
        Vector3[] outPosition, Quaternion[] outRotation, Vector3[] outScale) {
        int boneCount = bindLocal.Length;

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

    public AnimationClip RetargetTo(SkeletonData targetSkeleton) {
        BoneChannel[] src = Data.Channels;
        if (src is null || src.Length == 0) return this;

        bool hasNames = false;
        for (int i = 0; i < src.Length; i++)
            if (!string.IsNullOrEmpty(src[i].BoneName)) { hasNames = true; break; }
        if (!hasNames) return this;

        var remapped = new List<BoneChannel>(src.Length);
        for (int i = 0; i < src.Length; i++) {
            BoneChannel c = src[i];
            int targetIndex = targetSkeleton.IndexOf(c.BoneName);
            if (targetIndex < 0) continue;
            remapped.Add(new BoneChannel(targetIndex, c.BoneName, c.PositionKeys, c.RotationKeys, c.ScaleKeys));
        }

        var data = new AnimationClipData(Data.Name, Data.DurationTicks, Data.TicksPerSecond, remapped.ToArray());
        return new AnimationClip(data, Name);
    }

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

    public static void ComposeLocal(Vector3[] position, Quaternion[] rotation, Vector3[] scale, Matrix4[] outLocal) {
        for (var i = 0; i < outLocal.Length; i++)
            outLocal[i] =
                Matrix4.CreateScale(scale[i]) *
                Matrix4.CreateFromQuaternion(rotation[i]) *
                Matrix4.CreateTranslation(position[i]);
    }

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
        return Quaternion.Slerp(a.Value, b.Value, f);
    }

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
