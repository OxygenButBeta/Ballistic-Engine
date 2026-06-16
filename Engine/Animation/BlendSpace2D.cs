using System.Numerics;

namespace BallisticEngine;

// A 2D directional blend space (Unity's "2D Freeform Directional", the standard for Mixamo-style locomotion):
// each clip sits at a 2D sample POSITION (e.g. idle=(0,0), walkFwd=(0,1), strafeRight=(1,0), runFwd=(0,2)),
// and a 2D PARAMETER (the gameplay movement vector) picks blend weights so the character smoothly interpolates
// between the nearest directional clips. The controller sets the parameter each frame from input; the weights
// feed an AnimationMixer (P1) which produces the blended pose.
//
// Weighting = Gradient Band Interpolation (Rune Skovbo Johansen's algorithm, what Unity uses for freeform
// directional). For each pair (i, j) it measures how far the parameter is "past" sample i toward j using both
// the angular and magnitude difference, takes the min over all j as sample i's influence, and normalizes. This
// handles the hard cases a naive inverse-distance fails: the center/idle sample, samples at the same angle but
// different magnitude (walk vs run forward), and a parameter outside the convex hull.
public sealed class BlendSpace2D {
    public readonly struct Sample {
        public readonly AnimationClip Clip;
        public readonly Vector2 Position;   // where this clip lives in parameter space
        public Sample(AnimationClip clip, Vector2 position) { Clip = clip; Position = position; }
    }

    readonly List<Sample> samples = new();
    readonly AnimationMixer mixer = new();
    public AnimationMixer Mixer => mixer;
    public int Count => samples.Count;

    public Vector2 Parameter { get; private set; }

    // Adds a directional clip at `position` (e.g. (0,1) = forward, (1,0) = right, (0,0) = idle). The clip is
    // also added to the backing mixer (weight 0 until SetParameter runs). Loops by default (locomotion clips).
    public void Add(AnimationClip clip, Vector2 position, bool loop = true) {
        samples.Add(new Sample(clip, position));
        mixer.Add(clip, weight: 0f, loop: loop);
    }

    public void Clear() {
        samples.Clear();
        mixer.Clear();
    }

    // Sets the blend parameter (the movement vector) and recomputes per-sample weights via gradient-band
    // interpolation, writing them into the backing mixer. The driver then advances mixer time + evaluates.
    public void SetParameter(Vector2 p) {
        Parameter = p;
        int n = samples.Count;
        if (n == 0) return;
        if (n == 1) { mixer[0].Weight = 1f; return; }

        // Gradient Band Interpolation. For sample i, influence starts at 1 and is reduced by every other
        // sample j: h_ij = 1 - (p - Pi) . (Pj - Pi) / |Pj - Pi|^2, clamped to [0,1]. Sample i's weight is the
        // MIN of h_ij over all j (the most-constraining neighbour). Uses a center-aware vector form that works
        // for the (0,0) idle sample and same-direction different-magnitude samples.
        Span<float> w = n <= 32 ? stackalloc float[n] : new float[n];
        float totalWeight = 0f;

        for (int i = 0; i < n; i++) {
            Vector2 Pi = samples[i].Position;
            float weight = 1f;

            for (int j = 0; j < n; j++) {
                if (j == i) continue;
                Vector2 Pj = samples[j].Position;

                // Use a magnitude+angle aware difference (Johansen's "type 2" cartesian form): build the vectors
                // in a space where both radial distance and angle matter, so forward-walk vs forward-run (same
                // angle) and idle (zero magnitude) separate cleanly.
                Vector2 iToP = CartesianDelta(Pi, p);
                Vector2 iToJ = CartesianDelta(Pi, Pj);

                float lenSq = iToJ.LengthSquared();
                float h = lenSq > 1e-8f ? 1f - Vector2.Dot(iToP, iToJ) / lenSq : 1f;
                h = Math.Clamp(h, 0f, 1f);
                if (h < weight) weight = h;
            }

            w[i] = weight;
            totalWeight += weight;
        }

        // Normalize (if everything zeroed out — parameter far outside the hull — fall back to nearest sample).
        if (totalWeight <= 1e-6f) {
            int nearest = 0; float best = float.MaxValue;
            for (int i = 0; i < n; i++) {
                float d = Vector2.DistanceSquared(samples[i].Position, p);
                if (d < best) { best = d; nearest = i; }
            }
            for (int i = 0; i < n; i++) mixer[i].Weight = i == nearest ? 1f : 0f;
            return;
        }

        for (int i = 0; i < n; i++)
            mixer[i].Weight = w[i] / totalWeight;
    }

    // Johansen's directional-cartesian delta between two sample points as seen for the band between `from` and
    // a target: combines the angle between them and the (log-ish) magnitude ratio so radial and angular distance
    // are comparable. For the common case (one sample at origin) this degenerates to the plain vector delta.
    static Vector2 CartesianDelta(Vector2 from, Vector2 to) {
        float lenFrom = from.Length();
        float lenTo = to.Length();

        // Either endpoint at/near the origin -> there's no angle to measure (the origin has no direction), so
        // the band is purely radial: the plain cartesian difference. This is the idle-sample case and MUST be
        // handled before the acos below (dividing by a zero length there yields NaN weights).
        if (lenFrom < 1e-5f || lenTo < 1e-5f) return to - from;

        float avgLen = (lenFrom + lenTo) * 0.5f;
        // Angle between the two directions, scaled by average magnitude (so angular spread has cartesian units).
        float dot = Math.Clamp(Vector2.Dot(from, to) / (lenFrom * lenTo), -1f, 1f);
        float angle = MathF.Acos(dot);
        // Sign of the angle (which side `to` is on) from the 2D cross product.
        float cross = from.X * to.Y - from.Y * to.X;
        float signedAngle = cross < 0f ? -angle : angle;

        float radial = lenTo - lenFrom;
        float angular = signedAngle * avgLen;
        return new Vector2(radial, angular);
    }

    // Advances the backing mixer's time and evaluates the blended pose into the caller's TRS buffers.
    public void Advance(float dt) => mixer.AdvanceTime(dt);

    public void Evaluate(SkeletonData skeleton, Matrix4[] bindLocal,
        Vector3[] outPos, Quaternion[] outRot, Vector3[] outScale) =>
        mixer.Evaluate(skeleton, bindLocal, outPos, outRot, outScale);
}
