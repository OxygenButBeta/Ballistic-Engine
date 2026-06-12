using OpenTK.Mathematics;

namespace BallisticEngine;

// Plays AnimationClips over time and feeds the resulting skinning matrices to a SkinnedMeshRenderer
// on the same entity (Unity's Animator, simplified). Supports CROSSFADE between two clips: a fade
// blends the outgoing and incoming clips per-bone (lerp position/scale, slerp rotation), so
// idle->walk->run transitions are smooth instead of popping.
//
// Each frame: sample the active clip(s) -> per-bone local TRS -> blend if fading -> compose to local
// matrices -> walk the skeleton (local->world) -> skinning matrices = inverseBind * worldBone ->
// renderer. Play-mode only; bind pose in edit mode.
[Component("Animator", "Animation")]
public class Animator : Behaviour {
    [Tooltip("The clip to play. Drag a .banim animation asset here.")]
    public AnimationClip Clip { get; set; }

    [Tooltip("Restart the clip from the beginning when it ends.")]
    public bool Loop { get; set; } = true;

    [Tooltip("Play automatically when the scene begins.")]
    public bool PlayOnAwake { get; set; } = true;

    [Tooltip("Playback speed multiplier (1 = normal, 2 = double, negative = reverse).")]
    [Range(-4f, 4f)]
    public float Speed { get; set; } = 1f;

    [NotSerialized]
    public bool IsPlaying { get; private set; }

    [NotSerialized]
    public float Time { get; set; }

    // The currently-playing clip (the destination of a crossfade once it completes).
    AnimationClip activeClip;
    float activeTime;

    // The outgoing clip during a crossfade (null when not fading).
    AnimationClip fadeFromClip;
    float fadeFromTime;
    float fadeDuration;
    float fadeElapsed;

    SkinnedMeshRenderer renderer;

    // Scratch arrays (sized to the skeleton). TRS buffers for each blended clip + the final pose.
    Matrix4[] localPose, worldPose, skinning, bindLocal;
    Vector3[] posA, posB, scaleA, scaleB;
    Quaternion[] rotA, rotB;

    protected internal override void OnBegin() {
        renderer = GetComponent<SkinnedMeshRenderer>();
        if (PlayOnAwake && Clip is not null)
            Play(Clip);
    }

    // Plays a clip immediately (no fade). Defaults to the serialized Clip.
    public void Play() => Play(Clip);

    public void Play(AnimationClip clip) {
        activeClip = clip ?? Clip;
        Clip = activeClip;
        activeTime = 0f;
        Time = 0f;
        fadeFromClip = null;
        fadeElapsed = fadeDuration = 0f;
        IsPlaying = activeClip is not null;
    }

    // Smoothly crossfades from the current clip to `clip` over `duration` seconds (Unity's
    // Animator.CrossFade). A second CrossFade mid-fade snaps the in-progress blend as the new origin.
    public void CrossFade(AnimationClip clip, float duration) {
        if (clip is null) return;
        if (activeClip is null || duration <= 0f) {
            Play(clip);
            return;
        }
        // The outgoing layer is whatever is showing right now (the active clip at its current time).
        fadeFromClip = activeClip;
        fadeFromTime = activeTime;
        fadeDuration = duration;
        fadeElapsed = 0f;

        activeClip = clip;
        Clip = clip;
        activeTime = 0f;
        IsPlaying = true;
    }

    public void Stop() {
        IsPlaying = false;
        activeTime = Time = 0f;
        fadeFromClip = null;
    }

    public void Pause() => IsPlaying = false;

    protected internal override void Tick(in float delta) {
        if (!IsPlaying || activeClip is null)
            return;

        renderer ??= GetComponent<SkinnedMeshRenderer>();
        Mesh mesh = renderer?.SharedMesh;
        if (mesh is null || !mesh.IsSkinned)
            return;

        SkeletonData skeleton = mesh.Skeleton;
        int boneCount = skeleton.BoneCount;
        EnsureScratch(skeleton, boneCount);

        activeTime += delta * Speed;
        Time = activeTime;

        // Sample the incoming/active clip to TRS.
        activeClip.SampleLocalTRS(activeTime, Loop, bindLocal, posB, rotB, scaleB);

        if (fadeFromClip is not null) {
            // Advance + sample the outgoing clip, then blend by fade weight (0 = outgoing, 1 = active).
            fadeFromTime += delta * Speed;
            fadeElapsed += delta;
            float weight = MathHelper.Clamp(fadeElapsed / fadeDuration, 0f, 1f);

            fadeFromClip.SampleLocalTRS(fadeFromTime, Loop, bindLocal, posA, rotA, scaleA);
            for (var i = 0; i < boneCount; i++) {
                Vector3 p = Vector3.Lerp(posA[i], posB[i], weight);
                Quaternion r = Quaternion.Slerp(rotA[i], rotB[i], weight);
                Vector3 s = Vector3.Lerp(scaleA[i], scaleB[i], weight);
                localPose[i] = Matrix4.CreateScale(s) * Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p);
            }

            if (weight >= 1f)
                fadeFromClip = null; // fade complete
        }
        else {
            AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
        }

        // local -> world (mesh-local). Pre-order: parent computed before child.
        for (var i = 0; i < boneCount; i++) {
            int parent = skeleton.ParentIndices[i];
            worldPose[i] = parent >= 0 ? localPose[i] * worldPose[parent] : localPose[i];
        }

        // skinning matrix = inverseBind * worldBone (row-vector).
        for (var i = 0; i < boneCount; i++)
            skinning[i] = skeleton.InverseBindPose[i] * worldPose[i];

        renderer.SetSkinningMatrices(skinning);
    }

    void EnsureScratch(SkeletonData skeleton, int boneCount) {
        if (skinning is not null && skinning.Length == boneCount)
            return;
        localPose = new Matrix4[boneCount];
        worldPose = new Matrix4[boneCount];
        skinning = new Matrix4[boneCount];
        bindLocal = new Matrix4[boneCount];
        posA = new Vector3[boneCount]; posB = new Vector3[boneCount];
        scaleA = new Vector3[boneCount]; scaleB = new Vector3[boneCount];
        rotA = new Quaternion[boneCount]; rotB = new Quaternion[boneCount];
        for (var i = 0; i < boneCount; i++)
            bindLocal[i] = skeleton.BindPoseLocal[i];
    }
}
