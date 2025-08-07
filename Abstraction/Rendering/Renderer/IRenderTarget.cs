using BallisticEngine;

public interface IRenderTarget {
    Mesh CommonMesh { get; }
    Material Material { get; }
    Transform Transform { get; }
    public void Select();
}