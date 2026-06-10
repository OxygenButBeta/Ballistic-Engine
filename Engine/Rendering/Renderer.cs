namespace BallisticEngine;

public abstract class Renderer : Behaviour, IStaticMeshRenderer {
    public abstract Mesh SharedMesh { get; set; }
    public abstract Material SharedMaterial { get; set; }
    public Transform Transform => transform;
    public bool RenderedThisFrame { get; set; }

    public bool IsRenderable => SharedMesh is not null && SharedMaterial is not null;

    public void Activate() {
        SharedMaterial.Activate();
        SharedMesh.Activate();
    }

    public void Deactivate() {
        SharedMaterial.Deactivate();
        SharedMesh.Deactivate();
    }
}
