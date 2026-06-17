using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Single source for the Scene/Game offscreen render sequence. Both views run the IDENTICAL
// steps — clamp the panel size, resize the offscreen target only when it actually changed
// (the GLFrameBuffer.Resize discipline: a redundant resize deletes+recreates the texture and
// flickers the viewport), pick the render target, bind a camera, BeginRender, then cleanup.
// The two views differ ONLY in the camera and a Scene-only post step (gizmo depth grid), so
// the body is folded into ONE Render() core; RenderSceneView/RenderGameView in EditorApplication
// are thin callers. This kills the copy-paste so a third view is one call, not a third paste.
//
// State the renderer genuinely owns: the cached pixel size per target (sceneW/H, gameW/H) used
// to detect "size actually changed". The camera, view-projection adapter, and on-screen panel
// sizes stay in EditorApplication — they're referenced all over the UI/gizmo code.
internal sealed class ViewportRenderer {
    // Resolved per call (the active renderer is RenderAsset.Current.Renderer, which can change /
    // not exist at construction — same indirection EditorApplication.Renderer uses).
    readonly System.Func<HDRenderer> renderer;

    // Last pixel size each offscreen target was sized to (0 = never sized → forces a resize).
    int sceneW, sceneH, gameW, gameH;

    public ViewportRenderer(System.Func<HDRenderer> renderer) => this.renderer = renderer;

    // Both targets re-sync next frame (e.g. after the renderer/window changed underneath us).
    public void InvalidateTargetSizes() => sceneW = sceneH = gameW = gameH = 0;

    // The shared sequence. `postRender` runs after BeginRender, before cleanup (Scene uses it for
    // the gizmo depth grid; Game passes null). `renderSize` is the offscreen pixel resolution.
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

    // The Scene view renders from the editor's free-fly camera and publishes the coarse depth grid
    // for gizmo depth-occlusion while the Scene depth is still intact (gizmos drawn later this frame
    // dim when behind geometry). The caller sets the editor camera's aspect before this runs.
    public void RenderSceneView(IViewProjectionProvider editorCamera, SysVec2 renderSize) =>
        Render(HDRenderer.RenderTarget.Scene, editorCamera, renderSize, ref sceneW, ref sceneH,
               static r => r.ReadSceneDepthGrid());

    // The Game view renders from a scene HDCamera (via SceneCameraView in edit mode); no depth grid.
    public void RenderGameView(IViewProjectionProvider gameCamera, SysVec2 renderSize) =>
        Render(HDRenderer.RenderTarget.Game, gameCamera, renderSize, ref gameW, ref gameH, null);
}
