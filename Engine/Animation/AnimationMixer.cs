namespace BallisticEngine;

public sealed class AnimationMixer {
    public sealed class Input {
        public AnimationClip Clip;
        public float Time;
        public float Weight;
        public float Speed = 1f;
        public bool Loop = true;

        public Input() { }
        public Input(AnimationClip clip, float weight = 0f, bool loop = true) {
            Clip = clip; Weight = weight; Loop = loop;
        }
    }

    readonly List<Input> inputs = new();
    public IReadOnlyList<Input> Inputs => inputs;
    public int Count => inputs.Count;

    Vector3[][] pos, scale;
    Quaternion[][] rot;
    int scratchBones = -1;

    public Input Add(AnimationClip clip, float weight = 0f, bool loop = true) {
        var input = new Input(clip, weight, loop);
        inputs.Add(input);
        scratchBones = -1;
        return input;
    }

    public Input this[int i] => inputs[i];

    public void Clear() {
        inputs.Clear();
        scratchBones = -1;
    }

    public void AdvanceTime(float dt) {
        foreach (Input input in inputs)
            input.Time += dt * input.Speed;
    }

    public void Evaluate(SkeletonData skeleton, Matrix4[] bindLocal,
        Vector3[] outPos, Quaternion[] outRot, Vector3[] outScale) {
        int boneCount = bindLocal.Length;
        EnsureScratch(boneCount);

        float total = 0f;
        int activeCount = 0;
        for (int k = 0; k < inputs.Count; k++) {
            Input input = inputs[k];
            if (input.Clip is null || input.Weight <= 0f) continue;
            total += input.Weight;
            activeCount++;
        }

        if (activeCount == 0 || total <= 0f) {
            for (int b = 0; b < boneCount; b++) {
                outPos[b] = bindLocal[b].ExtractTranslation();
                outRot[b] = bindLocal[b].ExtractRotation();
                outScale[b] = bindLocal[b].ExtractScale();
            }
            return;
        }

        if (activeCount == 1) {
            for (int k = 0; k < inputs.Count; k++) {
                Input input = inputs[k];
                if (input.Clip is null || input.Weight <= 0f) continue;
                input.Clip.SampleLocalTRS(input.Time, input.Loop, bindLocal, outPos, outRot, outScale);
                return;
            }
        }

        int slot = 0;
        Span<float> norm = activeCount <= 16 ? stackalloc float[activeCount] : new float[activeCount];
        for (int k = 0; k < inputs.Count; k++) {
            Input input = inputs[k];
            if (input.Clip is null || input.Weight <= 0f) continue;
            input.Clip.SampleLocalTRS(input.Time, input.Loop, bindLocal, pos[slot], rot[slot], scale[slot]);
            norm[slot] = input.Weight / total;
            slot++;
        }

        for (int b = 0; b < boneCount; b++) {
            Vector3 p = Vector3.Zero, s = Vector3.Zero;
            Quaternion qAccum = default;
            bool seeded = false;

            for (int a = 0; a < activeCount; a++) {
                float w = norm[a];
                p += pos[a][b] * w;
                s += scale[a][b] * w;

                Quaternion q = rot[a][b];
                if (!seeded) {
                    qAccum = q * w;
                    seeded = true;
                }
                else {
                    if (Quaternion.Dot(qAccum, q) < 0f) q = -q;
                    qAccum += q * w;
                }
            }

            outPos[b] = p;
            outScale[b] = s;
            outRot[b] = qAccum.LengthSquared() > 1e-12f ? Quaternion.Normalize(qAccum) : Quaternion.Identity;
        }
    }

    void EnsureScratch(int boneCount) {
        if (scratchBones == boneCount && pos is not null && pos.Length == inputs.Count)
            return;
        int n = inputs.Count;
        pos = new Vector3[n][];
        scale = new Vector3[n][];
        rot = new Quaternion[n][];
        for (int i = 0; i < n; i++) {
            pos[i] = new Vector3[boneCount];
            scale[i] = new Vector3[boneCount];
            rot[i] = new Quaternion[boneCount];
        }
        scratchBones = boneCount;
    }
}
