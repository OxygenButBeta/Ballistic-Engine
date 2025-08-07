namespace BallisticEngine;

public abstract class Renderer : Behaviour, IRenderTarget {
    public abstract Mesh CommonMesh { get; protected set; }
    public abstract Material Material { get; protected set; }
    public Transform Transform => transform;
    public void Select() {
        Material.Texture.Activate();
        Material.Shader.Activate();
        CommonMesh.Activate();
    }
}