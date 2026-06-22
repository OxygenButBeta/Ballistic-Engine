
namespace BallisticEngine;

public class SkinnedMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; set; }
    public override Material SharedMaterial { get; set; }

    [HideInInspector]
    public override int SubMeshIndex { get; set; } = -1;

    [HideInInspector]
    public Material[] SharedMaterials { get => MaterialOverrides; set => MaterialOverrides = value; }

    Matrix4[] skinningMatrices;

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

    public void EnsureBindPose() {
        int count = SharedMesh is { IsSkinned: true } ? SharedMesh.BoneCount : 0;
        if (skinningMatrices is null || skinningMatrices.Length != count) {
            skinningMatrices = new Matrix4[count];
            for (var i = 0; i < count; i++)
                skinningMatrices[i] = Matrix4.Identity;
        }
    }

    public void SetSkinningMatrices(Matrix4[] matrices) {
        if (matrices is not null && SharedMesh is { IsSkinned: true } && matrices.Length == SharedMesh.BoneCount)
            skinningMatrices = matrices;
    }
}
