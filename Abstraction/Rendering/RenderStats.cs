namespace BallisticEngine;

// Live renderer statistics, one instance per render target (Scene/Game view). The renderer
// resets the submission counters at the top of each BeginRender and re-publishes GPU pass
// timings as their timestamp queries complete (a few frames of latency — queries are drained
// non-blocking, never stalling the pipeline). Editor UI reads these directly. BCL-only types:
// this lives in Abstraction so hosts can read it without touching GL.
public sealed class RenderStats {
    public static readonly RenderStats Scene = new();
    public static readonly RenderStats Game = new();

    // CPU submission counters for the most recently submitted frame. Depth-only draws covers
    // the shadow cascades, punctual shadow tiles and the z-prepass; bake draws are probe /
    // reflection captures (only non-zero while a bake is stepping).
    public int DrawCalls;
    public int DepthOnlyDrawCalls;
    public int InstancedDrawCalls;
    public int DrawsSavedByInstancing;
    public long Triangles;
    public int RenderersVisible;
    public int RenderersCulled;

    // GPU time per pass for the last completed frame (milliseconds). Replaced wholesale when
    // a frame's queries drain; GpuFrameMs spans first-to-last pass including gaps between them.
    public readonly List<(string Name, double Ms)> GpuPasses = new();
    public double GpuFrameMs;

    public void ResetSubmission() {
        DrawCalls = 0;
        DepthOnlyDrawCalls = 0;
        InstancedDrawCalls = 0;
        DrawsSavedByInstancing = 0;
        Triangles = 0;
        RenderersVisible = 0;
        RenderersCulled = 0;
    }
}
