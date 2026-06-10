using BallisticEngine.Rendering;
using OpenTK.Mathematics;

namespace BallisticEngine;

public abstract class HDRenderer {
    // When true (player), BeginRender blits the scene to the screen. When false (editor),
    // the scene stays in the offscreen color texture so a host can sample it (e.g. ImGui::Image).
    public bool PresentToScreen { get; set; } = true;

    // GL id of the offscreen color texture the scene renders into (for the editor viewport).
    public abstract int SceneColorTextureId { get; }

    // Resize the offscreen render target to match the editor viewport panel.
    public abstract void ResizeSceneTarget(int width, int height);

    public abstract void Initialize();
    public abstract void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args,bool isShadowPass);
    public abstract void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args);
    public abstract void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args);
    public abstract RenderMetrics BeginRender(RendererArgs args);
    public abstract void PostRenderCleanUp();
    public abstract void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args);
}