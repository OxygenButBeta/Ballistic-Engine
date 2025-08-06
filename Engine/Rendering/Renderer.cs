namespace BallisticEngine;

public abstract class Renderer : Behaviour, IRenderTarget {
    public abstract Mesh Mesh { get; protected set; }
    public abstract Material Material { get; protected set; }
    public Transform Transform => transform;
    public void Bind() {
        Material.Texture.Bind();
        Material.Shader.Bind();
        Mesh.Bind();
    }
}