using BallisticEngine.Rendering;
using OpenTK.Mathematics;

namespace BallisticEngine;

public abstract class HDRenderer {
    // When true (player), BeginRender blits the scene to the screen. When false (editor),
    // the scene stays in the offscreen color texture so a host can sample it (e.g. ImGui::Image).
    public bool PresentToScreen { get; set; } = true;

    // The editor renders the scene twice per frame into two offscreen targets: the Scene view
    // (editor camera) and the Game view (scene camera). Select which one BeginRender writes to.
    public enum RenderTarget { Scene, Game }
    public RenderTarget ActiveTarget { get; set; } = RenderTarget.Scene;

    // Renderer debug visualisations (editor "shading mode" dropdown). Shaded = the normal lit
    // pipeline; the rest replace the final image with a G-buffer channel (Normals/Depth) or draw
    // the opaque geometry as Wireframe — for inspecting the renderer / scene without lighting noise.
    // Set per-frame by the editor before BeginRender; the player always renders Shaded.
    public enum DebugView { Shaded, Wireframe, Normals, Depth }
    public DebugView DebugViewMode { get; set; } = DebugView.Shaded;

    // HDR -> display tunables (tonemap, bloom, SSAO, MSAA, grading). Shared by all targets.
    public PostProcessSettings PostFX { get; } = new();

    // GL ids of the two offscreen color textures (for ImGui::Image in the Scene/Game panels).
    public abstract int SceneColorTextureId { get; }
    public abstract int GameColorTextureId { get; }

    // Resize each offscreen target to match its editor panel.
    public abstract void ResizeSceneTarget(int width, int height);
    public abstract void ResizeGameTarget(int width, int height);

    public abstract void Initialize();
    public abstract void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args,bool isShadowPass);
    public abstract void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args);
    public abstract void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args);
    public abstract RenderMetrics BeginRender(RendererArgs args);
    public abstract void PostRenderCleanUp();
    public abstract void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args);
}