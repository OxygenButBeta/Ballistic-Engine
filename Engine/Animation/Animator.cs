
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

    // ---- Animation events (Unity's AnimationEvent) ---------------------------
    // Keyed moments in the clip's timeline that fire a callback — footsteps, weapon-fire sync, VFX
    // triggers. A script subscribes to OnEvent and adds events by time + name. Runtime-only (the
    // scene serializer doesn't round-trip a List<struct>; events are wired by script, like Unity's
    // programmatic AddEvent / the clip-baked events a future importer could add).

    public readonly record struct AnimationEvent(float Time, string Name);

    // Fired with the event's Name when playback crosses its Time. Subscribe in OnBegin.
    public event Action<string> OnEvent;

    readonly List<AnimationEvent> events = new();

    // Adds an event at `time` seconds into the clip (kept sorted by time).
    public void AddEvent(float time, string name) {
        var e = new AnimationEvent(MathF.Max(0f, time), name);
        int i = events.FindIndex(x => x.Time > e.Time);
        if (i < 0) events.Add(e);
        else events.Insert(i, e);
    }

    public void ClearEvents() => events.Clear();
    public int EventCount => events.Count;

    // The most recently fired event's name + the time it fired (for the editor inspector / debugging).
    [NotSerialized]
    public string LastFiredEvent { get; private set; }

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

        float prevTime = activeTime;
        activeTime += delta * Speed;
        Time = activeTime;

        FireEventsBetween(prevTime, activeTime, activeClip.DurationSeconds);

        // Sample the incoming/active clip to TRS.
        activeClip.SampleLocalTRS(activeTime, Loop, bindLocal, posB, rotB, scaleB);
        SolveAndApply(skeleton, boneCount, delta);
    }

    // Fires every event whose Time was crossed advancing from `from` to `to`. Loop-aware: when the
    // clip wraps (to > duration while looping), the window is split [from, duration) + [0, wrapped) so
    // events near the loop seam still fire exactly once per pass. Forward playback only (Speed >= 0);
    // reverse playback skips events (v1).
    void FireEventsBetween(float from, float to, float duration) {
        if (events.Count == 0 || OnEvent is null || to <= from || duration <= 0f)
            return;

        if (Loop && to >= duration) {
            float wrapped = to % duration;
            FireWindow(from, duration);            // tail of this pass
            // Any whole extra loops in one frame (huge dt) would each replay all events; cap at one.
            FireWindow(0f, wrapped);               // head of the next pass
            return;
        }
        FireWindow(from, to);
    }

    // Fires events in the half-open window [lo, hi). Inclusive of an event exactly at lo only when
    // lo == 0 (the very first frame), so an event at t=0 isn't missed on the first pass.
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

    // Evaluates the serialized Clip at an absolute time and applies the pose — for EDITOR PREVIEW
    // (the editor's Animator scrub/play), independent of play mode and crossfade. No-op without a
    // skinned renderer + clip.
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

    // Shared pose solve: blend the active clip's TRS (with any crossfade), compose to local, walk the
    // skeleton, and hand skinning matrices to the renderer.
    void SolveAndApply(SkeletonData skeleton, int boneCount, float delta) {
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

        WalkAndSkin(skeleton, boneCount);
    }

    // localPose -> world (mesh-local, pre-order) -> skinning matrices -> renderer.
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
