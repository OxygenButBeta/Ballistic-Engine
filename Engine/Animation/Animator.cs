
namespace BallisticEngine;

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

    AnimationClip activeClip;
    float activeTime;

    AnimationClip fadeFromClip;
    float fadeFromTime;
    float fadeDuration;
    float fadeElapsed;

    SkinnedMeshRenderer renderer;

    Matrix4[] localPose, worldPose, skinning, bindLocal;
    Vector3[] posA, posB, scaleA, scaleB;
    Quaternion[] rotA, rotB;

    public readonly record struct AnimationEvent(float Time, string Name);

    public event Action<string> OnEvent;

    readonly List<AnimationEvent> events = new();

    public void AddEvent(float time, string name) {
        var e = new AnimationEvent(MathF.Max(0f, time), name);
        int i = events.FindIndex(x => x.Time > e.Time);
        if (i < 0) events.Add(e);
        else events.Insert(i, e);
    }

    public void ClearEvents() => events.Clear();
    public int EventCount => events.Count;

    [NotSerialized]
    public string LastFiredEvent { get; private set; }

    protected internal override void OnBegin() {
        renderer = GetComponent<SkinnedMeshRenderer>();
        if (PlayOnAwake && Clip is not null)
            Play(Clip);
    }

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

    public void CrossFade(AnimationClip clip, float duration) {
        if (clip is null) return;
        if (activeClip is null || duration <= 0f) {
            Play(clip);
            return;
        }

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

        float prevTime = activeTime;
        activeTime += delta * Speed;
        Time = activeTime;

        FireEventsBetween(prevTime, activeTime, activeClip.DurationSeconds);

        activeClip.SampleLocalTRS(activeTime, Loop, bindLocal, posB, rotB, scaleB);
        SolveAndApply(skeleton, boneCount, delta);
    }

    void FireEventsBetween(float from, float to, float duration) {
        if (events.Count == 0 || OnEvent is null || to <= from || duration <= 0f)
            return;

        if (Loop && to >= duration) {
            float wrapped = to % duration;
            FireWindow(from, duration);
            FireWindow(0f, wrapped);
            return;
        }
        FireWindow(from, to);
    }

    void FireWindow(float lo, float hi) {
        foreach (AnimationEvent e in events) {
            bool inWindow = lo == 0f ? (e.Time >= lo && e.Time < hi) : (e.Time > lo && e.Time <= hi);
            if (inWindow) {
                LastFiredEvent = e.Name;
                try { OnEvent?.Invoke(e.Name); }
                catch (Exception ex) { Debugging.LogError($"Animation event '{e.Name}' handler threw: {ex}"); }
            }
        }
    }

    public void EvaluatePreview(float timeSeconds) {
        renderer ??= GetComponent<SkinnedMeshRenderer>();
        Mesh mesh = renderer?.SharedMesh;
        if (Clip is null || mesh is null || !mesh.IsSkinned)
            return;

        SkeletonData skeleton = mesh.Skeleton;
        int boneCount = skeleton.BoneCount;
        EnsureScratch(skeleton, boneCount);

        Clip.SampleLocalTRS(timeSeconds, Loop, bindLocal, posB, rotB, scaleB);
        AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
        WalkAndSkin(skeleton, boneCount);
    }

    void SolveAndApply(SkeletonData skeleton, int boneCount, float delta) {
        if (fadeFromClip is not null) {
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
                fadeFromClip = null;
        }
        else {
            AnimationClip.ComposeLocal(posB, rotB, scaleB, localPose);
        }

        WalkAndSkin(skeleton, boneCount);
    }

    void WalkAndSkin(SkeletonData skeleton, int boneCount) {
        for (var i = 0; i < boneCount; i++) {
            int parent = skeleton.ParentIndices[i];
            worldPose[i] = parent >= 0 ? localPose[i] * worldPose[parent] : localPose[i];
        }
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
