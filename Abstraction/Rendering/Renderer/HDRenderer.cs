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

    // EDITOR-ONLY extra debug visualisations (AO / lit-no-tonemap / SSGI / ... ) live in the EDITOR
    // project, not here, so they never ship in a player build. The renderer exposes this frame's
    // buffers through DebugFrame and asks EditorDebugComposite to draw — if it returns true it took
    // over the composite, otherwise the normal composite runs. The hook is null in the player (nothing
    // sets it), so this whole feature is dead weight there. The renderer NEVER references the editor
    // assembly; the editor wires the delegate at startup.
    // NOTE (DX12 migration): the int texture fields below are raw GL texture ids — an editor-only
    // DEBUG-composite path that binds G-buffer textures by GL handle. Left GL-coupled on purpose: the
    // editor debug composite is a Phase 7 concern (editor → DX12). Not part of the runtime display
    // contract (that's SceneColorHandle/GameColorHandle, now backend-agnostic).
    public struct DebugFrame {
        public int NormalTexture, DepthTexture, AoTexture, LitColor, SsgiTexture;   // GL texture ids (editor-debug, Phase 7)
        public int DestWidth, DestHeight;
        public bool PresentToScreen;     // true = draw into FB 0 (player); false = the editor display FBO
        public OpenTK.Mathematics.Matrix4 InvProjection;
        public int Mode;                 // editor-defined extra-mode index (0 = none / not an extra view)
    }

    // Set by the editor. Returns true if it drew the composite itself (the renderer then skips its own).
    public static Func<DebugFrame, bool> EditorDebugComposite;

    // The editor sets this to a non-zero extra-mode index when its dropdown picks an AO/Lit/etc. view;
    // 0 means "use the built-in DebugViewMode path". Lives here (not the editor) only so GLHDRenderer
    // can read it without an editor reference; it's never set in the player.
    public static int EditorExtraDebugMode;

    // GI per-system ISOLATE for editor A/B debugging: 0 = normal (all systems per their overrides),
    // 1 = ONLY light probes, 2 = ONLY reflection probes, 3 = ONLY Lumen. The renderer forces the other
    // two systems off when non-zero (applied AFTER the volume stack, like the env overrides) so you can
    // see exactly what each GI system contributes. Editor-set; never touched in the player.
    public enum GiIsolate { None, Probes, Reflections, Lumen }
    public static GiIsolate EditorGiIsolate;

    // HDR -> display tunables (tonemap, bloom, SSAO, MSAA, grading). Shared by all targets.
    public PostProcessSettings PostFX { get; } = new();

    // Opaque backend handles of the two offscreen color textures (for ImGui::Image in the Scene/Game
    // panels). The host passes these straight to ImGui without interpreting them — GL fills its texture
    // name, a DX12 backend its descriptor handle. (Was raw GL `int` — a backend leak into the editor.)
    public abstract RenderHandle SceneColorHandle { get; }
    public abstract RenderHandle GameColorHandle { get; }

    // Whether the Scene/Game color textures are stored top-down (row 0 = top of image). GL textures are
    // bottom-up (false → the editor flips V when sampling them in ImGui::Image); DX12 textures are
    // top-down (true → no flip). The editor reads this to orient the viewport image correctly per backend.
    public virtual bool DisplayTextureTopDown => false;

    // Resize each offscreen target to match its editor panel.
    public abstract void ResizeSceneTarget(int width, int height);
    public abstract void ResizeGameTarget(int width, int height);

    public abstract void Initialize();
    public abstract void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args,bool isShadowPass);
    public abstract void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args);
    public abstract void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args);
    public abstract RenderMetrics BeginRender(RendererArgs args);
    public abstract void PostRenderCleanUp();

    // Editor-only: publish a coarse Scene-view depth grid (into GizmoDepthOcclusion) for gizmo depth
    // occlusion. Called after BeginRender(Scene) while the depth buffer is intact. No-op by default.
    public virtual void ReadSceneDepthGrid() { }
    public abstract void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args);
}