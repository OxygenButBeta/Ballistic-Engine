namespace BallisticEngine;

public readonly struct RenderMetrics {
    public readonly int DrawCallCount;
    public readonly int ReducedDrawCallByInstancing;

    public readonly long VertexCount;
    public readonly long IndexCount;
    public readonly float RenderTime;

    public RenderMetrics(int drawCallCount, int vertexCount, int indexCount, int reducedDrawCallByInstancing,
        float renderTime) {
        DrawCallCount = drawCallCount;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        ReducedDrawCallByInstancing = reducedDrawCallByInstancing;
        RenderTime = renderTime;
    }
}