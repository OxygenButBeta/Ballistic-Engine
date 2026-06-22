using BallisticEngine.Rendering;

namespace BallisticEngine;

public abstract class HDRenderer {
    public bool PresentToScreen { get; set; } = true;

    public enum RenderTarget { Scene, Game }
    public RenderTarget ActiveTarget { get; set; } = RenderTarget.Scene;

    public enum DebugView { Shaded, Wireframe, Normals, Depth }
    public DebugView DebugViewMode { get; set; } = DebugView.Shaded;

    public struct DebugFrame {
        public int NormalTexture, DepthTexture, AoTexture, LitColor;
        public int DestWidth, DestHeight;
        public bool PresentToScreen;
        public Matrix4 InvProjection;
        public int Mode;
    }

    public static Func<DebugFrame, bool> EditorDebugComposite;

    public static int EditorExtraDebugMode;

    public PostProcessSettings PostFX { get; } = new();

    public abstract RenderHandle SceneColorHandle { get; }
    public abstract RenderHandle GameColorHandle { get; }

    public virtual bool DisplayTextureTopDown => false;

    public abstract void ResizeSceneTarget(int width, int height);
    public abstract void ResizeGameTarget(int width, int height);

    public abstract void Initialize();
    public abstract void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args,bool isShadowPass);
    public abstract void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args);
    public abstract void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args);
    public abstract RenderMetrics BeginRender(RendererArgs args);
    public abstract void PostRenderCleanUp();

    public virtual void ReadSceneDepthGrid() { }

    public virtual bool PollSurfaceReload() => false;
    public abstract void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args);
}
