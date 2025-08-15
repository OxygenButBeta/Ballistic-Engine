namespace BallisticEngine;

public abstract class Renderer : Behaviour, IStaticMeshRenderer {
    public abstract Mesh SharedMesh { get; protected set; }
    public abstract Material SharedMaterial { get; protected set; }
    public Transform Transform => transform;
    public bool RenderedThisFrame { get; set; }

    public void Activate() {
        SharedMaterial.Activate();
        SharedMesh.Activate();
    }

    public void Deactivate() {
        SharedMaterial.Deactivate();
        SharedMesh.Deactivate();
    }
}