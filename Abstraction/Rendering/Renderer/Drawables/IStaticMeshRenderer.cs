using BallisticEngine;

public interface IStaticMeshRenderer : IDrawable {
    Mesh SharedMesh { get; }
    Material SharedMaterial { get; }

    // False until both a mesh and a material have been assigned; the renderer skips such targets.
    bool IsRenderable { get; }

    public void Activate();
    public void Deactivate();
}
