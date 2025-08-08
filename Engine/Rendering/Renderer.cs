namespace BallisticEngine;

public abstract class Renderer : Behaviour, IOpaqueDrawable {
    public abstract Mesh SharedMesh { get; protected set; }
    public abstract Material SharedMaterial { get; protected set; }
    public Transform Transform => transform;
    public bool RenderedThisFrame { get; set; }

    public void Activate() {
        SharedMaterial.Shader.Activate();
        SharedMaterial.Texture.Activate();
        SharedMesh.Activate();
    }

    public void Deactivate() {
        SharedMesh.Deactivate();
        SharedMaterial.Texture.Deactivate();
        SharedMaterial.Shader.Deactivate();
    }
}