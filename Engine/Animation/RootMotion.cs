using System.Numerics;

namespace BallisticEngine;

// Extracts ROOT MOTION from an animation clip: the per-frame movement baked into the root bone's track. A
// root-motion locomotion clip (Mixamo's "In Place" OFF) animates the hips/root translating through the world
// so the feet stay planted; to turn that into game movement we read the root bone's delta each frame and apply
// it to the entity transform, then ZERO the root's in-place translation in the pose so the mesh doesn't move
// twice (Unity's "Apply Root Motion").
//
// Delta = root_local(toTime) - root_local(fromTime), with the loop seam handled: when playback wraps past the
// clip end the naive difference is a large backward jump, so the window is split [from -> duration] + [0 -> to]
// and the two deltas are summed. Rotation delta is the relative rotation fromRot^-1 * toRot.
//
// The delta is in the CLIP'S root-local space; the consumer rotates it by the entity's current world rotation
// so "forward in the clip" becomes "forward where the character faces". A pure helper — no component state.
public static class RootMotion {
    public readonly struct Delta {
        public readonly Vector3 Position;     // root-local translation moved this frame
        public readonly Quaternion Rotation;  // root-local rotation turned this frame
        public Delta(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        public static Delta Identity => new(Vector3.Zero, Quaternion.Identity);
    }

    // Computes the root-bone delta between two playback times on `clip`. `rootBoneIndex` is the root of the
    // skeleton (index 0 in the pre-order convention — the first bone with parent -1). `bindPos`/`bindRot` are
    // the root's bind-pose local TRS (the fallback for an un-keyed component).
    public static Delta Extract(AnimationClip clip, int rootBoneIndex, float fromTime, float toTime,
        bool loop, Vector3 bindPos, Quaternion bindRot) {
        if (clip is null || toTime == fromTime)
            return Delta.Identity;

        float duration = clip.DurationSeconds;

        // Non-looping (or no wrap): single window.
        if (!loop || duration <= 0f || (Floor(fromTime, duration) == Floor(toTime, duration))) {
            return Between(clip, rootBoneIndex, fromTime, toTime, loop, bindPos, bindRot);
        }

        // Looping with a wrap: sum [from -> end-of-its-loop] and [start -> to] across each loop boundary. In
        // practice dt is tiny so at most one boundary is crossed; handle the general case by walking loops.
        float fromWrapped = Wrap(fromTime, duration);
        float toWrapped = Wrap(toTime, duration);
        int loopsCrossed = Floor(toTime, duration) - Floor(fromTime, duration);

        // First partial: from fromWrapped to the clip end.
        Delta acc = Between(clip, rootBoneIndex, fromWrapped, duration, loop: false, bindPos, bindRot);
        // Any WHOLE loops in between each contribute a full-clip delta (rare; huge dt).
        if (loopsCrossed > 1) {
            Delta full = Between(clip, rootBoneIndex, 0f, duration, loop: false, bindPos, bindRot);
            for (int i = 1; i < loopsCrossed; i++)
                acc = Combine(acc, full);
        }
        // Final partial: from clip start to toWrapped.
        acc = Combine(acc, Between(clip, rootBoneIndex, 0f, toWrapped, loop: false, bindPos, bindRot));
        return acc;
    }

    // Delta over a window that does NOT cross a loop boundary.
    static Delta Between(AnimationClip clip, int rootBoneIndex, float fromTime, float toTime,
        bool loop, Vector3 bindPos, Quaternion bindRot) {
        clip.SampleBoneLocal(fromTime, rootBoneIndex, loop, bindPos, bindRot, out Vector3 p0, out Quaternion r0);
        clip.SampleBoneLocal(toTime, rootBoneIndex, loop, bindPos, bindRot, out Vector3 p1, out Quaternion r1);
        Vector3 dPos = p1 - p0;
        Quaternion dRot = Quaternion.Normalize(Quaternion.Conjugate(r0) * r1);
        return new Delta(dPos, dRot);
    }

    // Chains two sequential deltas (a then b): positions add (b is measured in the same root-local frame),
    // rotations compose.
    static Delta Combine(Delta a, Delta b) =>
        new(a.Position + b.Position, Quaternion.Normalize(a.Rotation * b.Rotation));

    static int Floor(float t, float duration) => (int)MathF.Floor(t / duration);
    static float Wrap(float t, float duration) {
        float w = t % duration;
        return w < 0f ? w + duration : w;
    }
}
