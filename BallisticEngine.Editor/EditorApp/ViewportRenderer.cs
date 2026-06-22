using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal sealed class ViewportRenderer {
    readonly System.Func<HDRenderer> renderer;

    int sceneW, sceneH, gameW, gameH;

    public ViewportRenderer(System.Func<HDRenderer> renderer) => this.renderer = renderer;

    public void InvalidateTargetSizes() => sceneW = sceneH = gameW = gameH = 0;

    void Render(HDRenderer.RenderTarget target, IViewProjectionProvider camera, SysVec2 renderSize,
               ref int cachedW, ref int cachedH, System.Action<HDRenderer> postRender) {
        HDRenderer r = renderer();
        int w = System.Math.Max(1, (int)renderSize.X);
        int h = System.Math.Max(1, (int)renderSize.Y);
        if (w != cachedW || h != cachedH) {
            if (target == HDRenderer.RenderTarget.Scene) r.ResizeSceneTarget(w, h);
            else r.ResizeGameTarget(w, h);
            cachedW = w;
            cachedH = h;
        }

        r.ActiveTarget = target;
        r.BeginRender(new RendererArgs(camera));
        postRender?.Invoke(r);
        r.PostRenderCleanUp();
    }

    public void RenderSceneView(IViewProjectionProvider editorCamera, SysVec2 renderSize) =>
        Render(HDRenderer.RenderTarget.Scene, editorCamera, renderSize, ref sceneW, ref sceneH,
               static r => r.ReadSceneDepthGrid());

    public void RenderGameView(IViewProjectionProvider gameCamera, SysVec2 renderSize) =>
        Render(HDRenderer.RenderTarget.Game, gameCamera, renderSize, ref gameW, ref gameH, null);
}
