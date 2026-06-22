namespace BallisticEngine;

public sealed class RenderStats {
    public static readonly RenderStats Scene = new();
    public static readonly RenderStats Game = new();

    public int DrawCalls;
    public int DepthOnlyDrawCalls;
    public int InstancedDrawCalls;
    public int DrawsSavedByInstancing;
    public long Triangles;
    public int RenderersVisible;
    public int RenderersCulled;

    public int SubMeshesCulled;

    public int PunctualLights;
    public int ShadowedLights;

    public readonly List<(string Name, double Ms)> GpuPasses = new();
    public double GpuFrameMs;

    public double CpuFrameMs;

    public void ResetSubmission() {
        DrawCalls = 0;
        DepthOnlyDrawCalls = 0;
        InstancedDrawCalls = 0;
        DrawsSavedByInstancing = 0;
        Triangles = 0;
        RenderersVisible = 0;
        RenderersCulled = 0;
        SubMeshesCulled = 0;
        PunctualLights = 0;
        ShadowedLights = 0;
    }
}
