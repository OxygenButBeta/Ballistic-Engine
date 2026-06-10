using BallisticEngine;

public interface IStaticMeshRenderer : IDrawable {
    Mesh SharedMesh { get; }
    Material SharedMaterial { get; }

    // False until both a mesh and a material have been assigned; the renderer skips such targets.
    bool IsRenderable { get; }

    // Entity active && component enabled — disabling either hides the mesh.
    bool IsActive { get; }

    public void Activate();
    public void Deactivate();
}
