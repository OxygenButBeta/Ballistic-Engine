using BallisticEngine;

public interface IRenderTarget {
    Mesh Mesh { get; }
    Material Material { get; }
    Transform Transform { get; }
}