namespace BallisticEngine;

[Component("Animancer", "Animation")]
public class AnimancerComponent : Behaviour {
    enum Source { None, Clip, CrossFade, Mixer, BlendSpace }

    [Tooltip("Clip to play automatically when the scene begins (optional — you usually drive playback from a script).")]
    public AnimationClip PlayOnAwakeClip { get; set; }

    [Tooltip("Playback speed multiplier applied to the active source (1 = normal).")]
    [Range(-4f, 4f)]
    public float Speed { get; set; } = 1f;

    [Tooltip("Apply the active clip's ROOT bone motion to the entity transform (Unity's Apply Root Motion). " +
             "Use the root-motion clip variants; the character then moves by the animation, not code velocity.")]
    public bool ApplyRootMotion { get; set; } = false;

    [NotSerialized] public float Time { get; private set; }
    [NotSerialized] public bool IsPlaying { get; private set; }

    [NotSerialized] public Vector3 LastRootDeltaPosition { get; private set; }
    [NotSerialized] public Quaternion LastRootDeltaRotation { get; private set; } = Quaternion.Identity;

    Source source = Source.None;
    SkinnedMeshRenderer renderer;

    AnimationClip activeClip;
    float activeTime;
    AnimationClip fadeFromClip;
    float fadeFromTime, fadeDuration, fadeElapsed;

    AnimationMixer mixer;
    BlendSpace2D blendSpace;

    Matrix4[] localPose, worldPose, skinning, bindLocal;
    Vector3[] posA, posB, scaleA, scaleB;
    Quaternion[] rotA, rotB;
    bool rootMotionPrimed;
    float prevRootSampleTime;

    readonly Dictionary<AnimationClip, AnimationClip> retargetCache = new();

    public AnimationClip Retarget(AnimationClip clip) {
        if (clip is null) return null;
        renderer ??= GetComponent<SkinnedMeshRenderer>();
        Mesh mesh = renderer?.SharedMesh;
        if (mesh is null || !mesh.IsSkinned) return clip;
        SkeletonData skel = mesh.Skeleton;

        if (retargetCache.TryGetValue(clip, out AnimationClip cached)) return cached;
        AnimationClip result = clip.MatchesSkeleton(skel) ? clip : clip.RetargetTo(skel);
        retargetCache[clip] = result;
        return result;
    }

    public void Play(AnimationClip clip, float fade = 0f) {
        if (clip is null) return;
        clip = Retarget(clip);
        if (fade <= 0f || activeClip is null) {
            activeClip = clip;
            activeTime = 0f;
            fadeFromClip = null;
            source = Source.Clip;
        }
        else {
            fadeFromClip = activeClip;
            fadeFromTime = activeTime;
            fadeDuration = fade;
            fadeElapsed = 0f;
            activeClip = clip;
            activeTime = 0f;
            source = Source.CrossFade;
        }
        IsPlaying = true;
        rootMotionPrimed = false;
    }

    public AnimationMixer PlayMixer(AnimationMixer m) {
        mixer = m;
        source = Source.Mixer;
        IsPlaying = m is not null;
        rootMotionPrimed = false;
        return m;
    }

    public BlendSpace2D PlayBlendSpace(BlendSpace2D bs) {
        blendSpace = bs;
        source = Source.BlendSpace;
        IsPlaying = bs is not null;
        rootMotionPrimed = false;
        return bs;
    }

    public AnimationMixer Mixer => mixer;
    public BlendSpace2D BlendSpace => blendSpace;

    public void Stop() {
        IsPlaying = false;
        source = Source.None;
        activeTime = Time = 0f;
        fadeFromClip = null;
    }

    protected internal override void OnBegin() {
        renderer = GetComponent<SkinnedMeshRenderer>();
        if (PlayOnAwakeClip is not null)
            Play(PlayOnAwakeClip);
    }

    protected internal override void Tick(in float delta) {
        if (!IsPlaying || source == Source.None) return;

        renderer ??= GetComponent<SkinnedMeshRenderer>();
        Mesh mesh = renderer?.SharedMesh;
        if (mesh is null || !mesh.IsSkinned) return;

        SkeletonData skeleton = mesh.Skeleton;
        int boneCount = skeleton.BoneCount;
        EnsureScratch(skeleton, boneCount);

        float dt = delta * Speed;

        ExtractRootMotion(skeleton, dt);

        switch (source) {
            case Source.Clip:
                activeTime += dt;
                Time = activeTime;
                activeClip.SampleLocalTRS(activeTime, loop: true, bindLocal, posB, rotB, scaleB);
                AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
                break;

            case Source.CrossFade:
                activeTime += dt;
                Time = activeTime;
                activeClip.SampleLocalTRS(activeTime, loop: true, bindLocal, posB, rotB, scaleB);
                if (fadeFromClip is not null) {
                    fadeFromTime += dt;
                    fadeElapsed += delta;
                    float weight = Math.Clamp(fadeElapsed / fadeDuration, 0f, 1f);
                    fadeFromClip.SampleLocalTRS(fadeFromTime, loop: true, bindLocal, posA, rotA, scaleA);
                    for (int i = 0; i < boneCount; i++) {
                        Vector3 p = Vector3.Lerp(posA[i], posB[i], weight);
                        Quaternion r = Quaternion.Slerp(rotA[i], rotB[i], weight);
                        Vector3 s = Vector3.Lerp(scaleA[i], scaleB[i], weight);
                        localPose[i] = Compose(p, r, s);
                    }
                    if (weight >= 1f) { fadeFromClip = null; source = Source.Clip; }
                }
                else {
                    AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
                }
                break;

            case Source.Mixer:
                mixer.AdvanceTime(dt);
                mixer.Evaluate(skeleton, bindLocal, posB, rotB, scaleB);
                AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
                Time += dt;
                break;

            case Source.BlendSpace:
                blendSpace.Advance(dt);
                blendSpace.Evaluate(skeleton, bindLocal, posB, rotB, scaleB);
                AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
                Time += dt;
                break;
        }

        if (ApplyRootMotion && boneCount > 0)
            localPose[0] = bindLocal[0];

        WalkAndSkin(skeleton, boneCount);
    }

    void ExtractRootMotion(SkeletonData skeleton, float dt) {
        LastRootDeltaPosition = Vector3.Zero;
        LastRootDeltaRotation = Quaternion.Identity;
        if (!ApplyRootMotion || dt == 0f) return;

        AnimationClip clip = PrimaryClip(out float clipTime);
        if (clip is null) return;

        Vector3 bindPos = skeleton.BindPoseLocal[0].ExtractTranslation();
        Quaternion bindRot = skeleton.BindPoseLocal[0].ExtractRotation();

        if (!rootMotionPrimed) { prevRootSampleTime = clipTime; rootMotionPrimed = true; }
        float from = prevRootSampleTime;
        float to = clipTime + dt;
        prevRootSampleTime = to;

        RootMotion.Delta d = RootMotion.Extract(clip, 0, from, to, loop: true, bindPos, bindRot);
        LastRootDeltaPosition = d.Position;
        LastRootDeltaRotation = d.Rotation;

        Quaternion worldRot = transform.Rotation;
        Vector3 worldDelta = Vector3.Transform(d.Position, worldRot);
        transform.Position += worldDelta;
        transform.Rotation = Quaternion.Normalize(worldRot * d.Rotation);
    }

    AnimationClip PrimaryClip(out float time) {
        switch (source) {
            case Source.Clip:
            case Source.CrossFade:
                time = activeTime;
                return activeClip;
            case Source.Mixer: {
                AnimationMixer.Input best = Dominant(mixer);
                time = best?.Time ?? 0f;
                return best?.Clip;
            }
            case Source.BlendSpace: {
                AnimationMixer.Input best = Dominant(blendSpace?.Mixer);
                time = best?.Time ?? 0f;
                return best?.Clip;
            }
            default:
                time = 0f;
                return null;
        }
    }

    static AnimationMixer.Input Dominant(AnimationMixer m) {
        if (m is null || m.Count == 0) return null;
        AnimationMixer.Input best = null; float bestW = -1f;
        for (int i = 0; i < m.Count; i++) {
            AnimationMixer.Input input = m[i];
            if (input.Weight > bestW) { bestW = input.Weight; best = input; }
        }
        return best;
    }

    void WalkAndSkin(SkeletonData skeleton, int boneCount) {
        for (int i = 0; i < boneCount; i++) {
            int parent = skeleton.ParentIndices[i];
            worldPose[i] = parent >= 0 ? localPose[i] * worldPose[parent] : localPose[i];
        }
        for (int i = 0; i < boneCount; i++)
            skinning[i] = skeleton.InverseBindPose[i] * worldPose[i];
        renderer.SetSkinningMatrices(skinning);
    }

    static Matrix4 Compose(Vector3 p, Quaternion r, Vector3 s) =>
        Matrix4.CreateScale(s) * Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p);

    void EnsureScratch(SkeletonData skeleton, int boneCount) {
        if (skinning is not null && skinning.Length == boneCount) return;
        localPose = new Matrix4[boneCount];
        worldPose = new Matrix4[boneCount];
        skinning = new Matrix4[boneCount];
        bindLocal = new Matrix4[boneCount];
        posA = new Vector3[boneCount]; posB = new Vector3[boneCount];
        scaleA = new Vector3[boneCount]; scaleB = new Vector3[boneCount];
        rotA = new Quaternion[boneCount]; rotB = new Quaternion[boneCount];
        for (int i = 0; i < boneCount; i++)
            bindLocal[i] = skeleton.BindPoseLocal[i];
    }
}
