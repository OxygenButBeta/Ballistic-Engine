using OpenTK.Mathematics;

namespace BallisticEngine;

// Plays an AnimationClip over time and feeds the resulting skinning matrices to a
// SkinnedMeshRenderer on the same entity (Unity's Animator, simplified). Each frame it:
//   1. samples the clip at the current time -> per-bone LOCAL transforms
//   2. walks the skeleton pre-order (parent < child) -> per-bone WORLD (mesh-local) matrices
//   3. forms skinning matrices = inverseBind[i] * worldBone[i] (row-vector: bind-space -> animated)
//   4. hands them to the renderer, which uploads them to the bone SSBO.
//
// Play-mode only (like every Behaviour Tick). In edit mode the renderer shows bind pose. With no
// clip or a clip whose bones don't match the mesh skeleton, it leaves bind pose untouched.
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

    SkinnedMeshRenderer renderer;

    // Scratch arrays reused each frame (sized to the skeleton). The skinning array is handed to the
    // renderer; a fresh one is allocated only when the bone count changes.
    Matrix4[] localPose;
    Matrix4[] worldPose;
    Matrix4[] skinning;
    Matrix4[] bindLocal;

    protected internal override void OnBegin() {
        renderer = GetComponent<SkinnedMeshRenderer>();
        if (PlayOnAwake)
            Play();
    }

    public void Play() {
        IsPlaying = true;
        Time = 0f;
    }

    public void Stop() {
        IsPlaying = false;
        Time = 0f;
    }

    public void Pause() => IsPlaying = false;

    protected internal override void Tick(in float delta) {
        if (!IsPlaying || Clip is null)
            return;

        renderer ??= GetComponent<SkinnedMeshRenderer>();
        Mesh mesh = renderer?.SharedMesh;
        if (mesh is null || !mesh.IsSkinned)
            return;

        SkeletonData skeleton = mesh.Skeleton;
        int boneCount = skeleton.BoneCount;
        EnsureScratch(skeleton, boneCount);

        Time += delta * Speed;

        // 1. sample -> local pose (un-keyed bones keep bind-local)
        Clip.Sample(Time, Loop, bindLocal, localPose);

        // 2. local -> world (mesh-local). Pre-order guarantees parent computed before child.
        for (var i = 0; i < boneCount; i++) {
            int parent = skeleton.ParentIndices[i];
            // Row-vector composition (matches Transform.WorldMatrix): child-local FIRST, then parent.
            worldPose[i] = parent >= 0 ? localPose[i] * worldPose[parent] : localPose[i];
        }

        // 3. skinning matrix = inverseBind * worldBone (row-vector: vertex * invBind * world).
        for (var i = 0; i < boneCount; i++)
            skinning[i] = skeleton.InverseBindPose[i] * worldPose[i];

        // 4. hand to the renderer (uploaded to the bone SSBO at draw).
        renderer.SetSkinningMatrices(skinning);
    }

    void EnsureScratch(SkeletonData skeleton, int boneCount) {
        if (skinning is not null && skinning.Length == boneCount)
            return;
        localPose = new Matrix4[boneCount];
        worldPose = new Matrix4[boneCount];
        skinning = new Matrix4[boneCount];
        bindLocal = new Matrix4[boneCount];
        for (var i = 0; i < boneCount; i++)
            bindLocal[i] = skeleton.BindPoseLocal[i];
    }
}
