namespace BallisticEngine;

public sealed class BlendSpace2D {
    public readonly struct Sample {
        public readonly AnimationClip Clip;
        public readonly Vector2 Position;
        public Sample(AnimationClip clip, Vector2 position) { Clip = clip; Position = position; }
    }

    readonly List<Sample> samples = new();
    readonly AnimationMixer mixer = new();
    public AnimationMixer Mixer => mixer;
    public int Count => samples.Count;

    public Vector2 Parameter { get; private set; }

    public void Add(AnimationClip clip, Vector2 position, bool loop = true) {
        samples.Add(new Sample(clip, position));
        mixer.Add(clip, weight: 0f, loop: loop);
    }

    public void Clear() {
        samples.Clear();
        mixer.Clear();
    }

    public void SetParameter(Vector2 p) {
        Parameter = p;
        int n = samples.Count;
        if (n == 0) return;
        if (n == 1) { mixer[0].Weight = 1f; return; }

        Span<float> w = n <= 32 ? stackalloc float[n] : new float[n];
        float totalWeight = 0f;

        for (int i = 0; i < n; i++) {
            Vector2 Pi = samples[i].Position;
            float weight = 1f;

            for (int j = 0; j < n; j++) {
                if (j == i) continue;
                Vector2 Pj = samples[j].Position;

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

    static Vector2 CartesianDelta(Vector2 from, Vector2 to) {
        float lenFrom = from.Length();
        float lenTo = to.Length();

        if (lenFrom < 1e-5f || lenTo < 1e-5f) return to - from;

        float avgLen = (lenFrom + lenTo) * 0.5f;
        float dot = Math.Clamp(Vector2.Dot(from, to) / (lenFrom * lenTo), -1f, 1f);
        float angle = MathF.Acos(dot);
        float cross = from.X * to.Y - from.Y * to.X;
        float signedAngle = cross < 0f ? -angle : angle;

        float radial = lenTo - lenFrom;
        float angular = signedAngle * avgLen;
        return new Vector2(radial, angular);
    }

    public void Advance(float dt) => mixer.AdvanceTime(dt);

    public void Evaluate(SkeletonData skeleton, Matrix4[] bindLocal,
        Vector3[] outPos, Quaternion[] outRot, Vector3[] outScale) =>
        mixer.Evaluate(skeleton, bindLocal, outPos, outRot, outScale);
}
