namespace BallisticEngine;

public abstract class Renderer : Behaviour, IRenderTarget {
    public static HashSet<IRenderTarget> RenderTargets { get; } = [];
    public abstract Mesh Mesh { get; protected set; }
    public abstract Material Material { get; protected set; }
    public Transform Transform => transform;
}