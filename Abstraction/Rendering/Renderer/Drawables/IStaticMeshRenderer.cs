using BallisticEngine;

public interface IStaticMeshRenderer : IDrawable {
    Mesh SharedMesh { get; }
    Material SharedMaterial { get; }
    public void Activate();
    public void Deactivate();
}