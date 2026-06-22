namespace BallisticEngine;

public static class RootMotion {
    public readonly struct Delta {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public Delta(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        public static Delta Identity => new(Vector3.Zero, Quaternion.Identity);
    }

    public static Delta Extract(AnimationClip clip, int rootBoneIndex, float fromTime, float toTime,
        bool loop, Vector3 bindPos, Quaternion bindRot) {
        if (clip is null || toTime == fromTime)
            return Delta.Identity;

        float duration = clip.DurationSeconds;

        if (!loop || duration <= 0f || (Floor(fromTime, duration) == Floor(toTime, duration))) {
            return Between(clip, rootBoneIndex, fromTime, toTime, loop, bindPos, bindRot);
        }

        float fromWrapped = Wrap(fromTime, duration);
        float toWrapped = Wrap(toTime, duration);
        int loopsCrossed = Floor(toTime, duration) - Floor(fromTime, duration);

        Delta acc = Between(clip, rootBoneIndex, fromWrapped, duration, loop: false, bindPos, bindRot);
        if (loopsCrossed > 1) {
            Delta full = Between(clip, rootBoneIndex, 0f, duration, loop: false, bindPos, bindRot);
            for (int i = 1; i < loopsCrossed; i++)
                acc = Combine(acc, full);
        }

        acc = Combine(acc, Between(clip, rootBoneIndex, 0f, toWrapped, loop: false, bindPos, bindRot));
        return acc;
    }

    static Delta Between(AnimationClip clip, int rootBoneIndex, float fromTime, float toTime,
        bool loop, Vector3 bindPos, Quaternion bindRot) {
        clip.SampleBoneLocal(fromTime, rootBoneIndex, loop, bindPos, bindRot, out Vector3 p0, out Quaternion r0);
        clip.SampleBoneLocal(toTime, rootBoneIndex, loop, bindPos, bindRot, out Vector3 p1, out Quaternion r1);
        Vector3 dPos = p1 - p0;
        Quaternion dRot = Quaternion.Normalize(Quaternion.Conjugate(r0) * r1);
        return new Delta(dPos, dRot);
    }

    static Delta Combine(Delta a, Delta b) =>
        new(a.Position + b.Position, Quaternion.Normalize(a.Rotation * b.Rotation));

    static int Floor(float t, float duration) => (int)MathF.Floor(t / duration);
    static float Wrap(float t, float duration) {
        float w = t % duration;
        return w < 0f ? w + duration : w;
    }
}
