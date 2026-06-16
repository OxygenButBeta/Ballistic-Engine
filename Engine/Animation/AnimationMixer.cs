namespace BallisticEngine;

// Blends N animation clips into one pose by per-input weights — the "mixer" (Unity's blend tree, Animancer's
// MixerState). This is the one piece of "smart blending" a code-driven (no state-machine) animation system
// can't avoid: 8-directional locomotion, idle<->walk<->run, aim offsets — all are N clips blended by weights
// the gameplay code computes from a movement vector, NOT a graph of transition arrows.
//
// Pure CPU + allocation-free into caller buffers (sized to the skeleton), exactly like AnimationClip.Sample.
// The blend is done in TRS space (lerp position/scale, weighted-normalized slerp rotation) because a matrix
// lerp can't blend rotation correctly. Bones no input animates fall back to the skeleton's bind-pose local.
//
// The mixer does NOT own time advancement or skeleton walking — the Animator/AnimancerComponent drives it:
// it sets each input's Time + Weight per frame, calls Evaluate to get blended local TRS, then composes +
// walks the skeleton + forms skinning matrices (the same back half as a single-clip play).
public sealed class AnimationMixer {
    // One clip feeding the blend. Time/Weight/Speed/Loop are set by the driver each frame.
    public sealed class Input {
        public AnimationClip Clip;
        public float Time;      // playback time in seconds (the driver advances it)
        public float Weight;    // blend weight (un-normalized; the mixer normalizes across active inputs)
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

    // Per-input scratch TRS buffers (resized with the skeleton). [input][bone].
    Vector3[][] pos, scale;
    Quaternion[][] rot;
    int scratchBones = -1;

    public Input Add(AnimationClip clip, float weight = 0f, bool loop = true) {
        var input = new Input(clip, weight, loop);
        inputs.Add(input);
        scratchBones = -1;   // force scratch resize (input count changed)
        return input;
    }

    public Input this[int i] => inputs[i];

    public void Clear() {
        inputs.Clear();
        scratchBones = -1;
    }

    // Advances every input's Time by dt*Speed. Convenience for drivers that want uniform time progress; a
    // driver that synchronizes phases (e.g. foot-locked locomotion) can advance Times itself instead.
    public void AdvanceTime(float dt) {
        foreach (Input input in inputs)
            input.Time += dt * input.Speed;
    }

    // Blends all inputs with Weight > 0 into outPos/outRot/outScale (length == skeleton.BoneCount). Weights
    // are normalized across the active inputs (so weights {2,1} == {0.667,0.333}); if none are active, the
    // output is the bind pose. Rotation uses hemisphere-aligned weighted accumulation then a normalize, the
    // standard robust N-quaternion blend (slerp is only exact for two, but this matches Unity blend trees).
    public void Evaluate(SkeletonData skeleton, Matrix4[] bindLocal,
        Vector3[] outPos, Quaternion[] outRot, Vector3[] outScale) {
        int boneCount = bindLocal.Length;
        EnsureScratch(boneCount);

        // Collect active inputs + total weight.
        float total = 0f;
        int activeCount = 0;
        for (int k = 0; k < inputs.Count; k++) {
            Input input = inputs[k];
            if (input.Clip is null || input.Weight <= 0f) continue;
            total += input.Weight;
            activeCount++;
        }

        // No active input -> bind pose.
        if (activeCount == 0 || total <= 0f) {
            for (int b = 0; b < boneCount; b++) {
                outPos[b] = bindLocal[b].ExtractTranslation();
                outRot[b] = bindLocal[b].ExtractRotation();
                outScale[b] = bindLocal[b].ExtractScale();
            }
            return;
        }

        // Single active input -> sample straight through (no blend cost, exact).
        if (activeCount == 1) {
            for (int k = 0; k < inputs.Count; k++) {
                Input input = inputs[k];
                if (input.Clip is null || input.Weight <= 0f) continue;
                input.Clip.SampleLocalTRS(input.Time, input.Loop, bindLocal, outPos, outRot, outScale);
                return;
            }
        }

        // Sample each active input's clip to its scratch TRS.
        int slot = 0;
        Span<float> norm = activeCount <= 16 ? stackalloc float[activeCount] : new float[activeCount];
        for (int k = 0; k < inputs.Count; k++) {
            Input input = inputs[k];
            if (input.Clip is null || input.Weight <= 0f) continue;
            input.Clip.SampleLocalTRS(input.Time, input.Loop, bindLocal, pos[slot], rot[slot], scale[slot]);
            norm[slot] = input.Weight / total;
            slot++;
        }

        // Blend per bone: weighted-sum position/scale, hemisphere-aligned weighted-accumulate + normalize
        // rotation. The first active input seeds the rotation accumulator's hemisphere.
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
                    // Align to the accumulator's hemisphere (q and -q are the same rotation) so the
                    // weighted sum doesn't cancel near 180-degree-apart quaternions.
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
