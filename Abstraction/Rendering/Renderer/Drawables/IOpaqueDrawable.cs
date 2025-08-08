using BallisticEngine;

public interface IOpaqueDrawable : IDrawable {
    Mesh SharedMesh { get; }
    Material SharedMaterial { get; }
    public void Activate();
    public void Deactivate();
}