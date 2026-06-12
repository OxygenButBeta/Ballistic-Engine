using OpenTK.Mathematics;

namespace BallisticEngine;

// Renders a skinned mesh (Unity's SkinnedMeshRenderer). Like StaticMeshRenderer it draws an assigned
// mesh + material, but it also exposes per-bone skinning matrices that the draw path uploads to the
// bone SSBO. An Animator on the same entity writes the matrices each frame; with no Animator (or in
// edit mode) it draws at BIND POSE (identity skinning matrices), which is exactly the mesh as
// authored — so a skinned mesh is visible in the editor without playing.
public class SkinnedMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; set; }
    public override Material SharedMaterial { get; set; }

    [HideInInspector]
    public override int SubMeshIndex { get; set; } = -1;

    // Per-bone skinning matrices for this frame (mesh-bind -> animated, mesh-local space). Identity
    // until an Animator drives them; size tracks the mesh's bone count. Runtime-only.
    Matrix4[] skinningMatrices;

    // IStaticMeshRenderer skinned hooks the draw path reads. IsSkinned is true once a skinned mesh is
    // assigned; SkinningMatrices is the per-frame pose (bind pose / identity until an Animator runs).
    public override bool IsSkinned => SharedMesh is { IsSkinned: true };
    public override Matrix4[] SkinningMatrices => skinningMatrices;

    protected internal override void OnAttach() {
        if (!RuntimeSet<IStaticMeshRenderer>.Contains(this))
            RuntimeSet<IStaticMeshRenderer>.Add(this);
        EnsureBindPose();
    }

    protected internal override void OnDetach() {
        RuntimeSet<IStaticMeshRenderer>.Remove(this);
    }

    // Resets SkinningMatrices to identity sized to the mesh's bones (bind pose). Called on attach and
    // whenever the mesh changes; an Animator overwrites it with sampled poses.
    public void EnsureBindPose() {
        int count = SharedMesh is { IsSkinned: true } ? SharedMesh.BoneCount : 0;
        if (skinningMatrices is null || skinningMatrices.Length != count) {
            skinningMatrices = new Matrix4[count];
            for (var i = 0; i < count; i++)
                skinningMatrices[i] = Matrix4.Identity;
        }
    }

    // Called by the Animator with freshly-computed skinning matrices. Ignored if the size disagrees
    // with the mesh (a stale clip) — bind pose stays.
    public void SetSkinningMatrices(Matrix4[] matrices) {
        if (matrices is not null && SharedMesh is { IsSkinned: true } && matrices.Length == SharedMesh.BoneCount)
            skinningMatrices = matrices;
    }
}
