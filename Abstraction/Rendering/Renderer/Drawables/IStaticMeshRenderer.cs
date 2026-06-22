using BallisticEngine;

public interface IStaticMeshRenderer : IDrawable {
    Mesh SharedMesh { get; }
    Material SharedMaterial { get; }

    int SubMeshIndex { get; }

    float LodBias => 1f;

    Material MaterialFor(int submeshIndex);

    bool IsRenderable { get; }

    bool IsActive { get; }

    bool IsSkinned => false;
    Matrix4[] SkinningMatrices => null;

    public void Activate();
    public void Deactivate();
}
