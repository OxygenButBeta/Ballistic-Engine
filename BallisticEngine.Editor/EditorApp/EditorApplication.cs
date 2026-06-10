using BallisticEngine.Serialization;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Drives the editor: brings the engine up, renders the scene twice per frame (Scene view via the
// editor camera, Game view via the scene camera) into offscreen targets, and presents them in
// ImGui panels alongside the Hierarchy, Inspector and Asset Browser.
internal sealed class EditorApplication {
    readonly IBallisticEngineRuntime runtime;
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;
    readonly EditorCamera editorCamera = new();
    readonly EditorInput editorInput;
    readonly EditorState editorState = new();

    readonly HierarchyPanel hierarchy;
    readonly InspectorPanel inspector;
    readonly AssetBrowserPanel assets = new();

    HDRenderer Renderer => RenderAsset.Current.Renderer;

    SysVec2 sceneViewSize = new(1280, 720);
    SysVec2 gameViewSize = new(1280, 720);
    int sceneW, sceneH, gameW, gameH;
    bool sceneViewHovered;

    public EditorApplication(GLBallisticEngineWindow window, string projectPath) {
        runtime = window;
        bootstrap = new EngineBootstrap(window, projectPath);
        imgui = new ImGuiController(window);
        editorInput = new EditorInput(window);
        hierarchy = new HierarchyPanel(editorState);
        inspector = new InspectorPanel(editorState);

        bootstrap.LoadStartupScene();
        Renderer.PresentToScreen = false; // editor presents into panels, not the screen

        window.OnResizeCallback += (w, h) => imgui.WindowResized(w, h);
        imgui.WindowResized(window.Width, window.Height);

        runtime.Window.SetFrequency(0);
        runtime.WindowUpdateCallback += OnUpdate;
        runtime.WindowRenderCallback += OnRender;
    }

    void OnUpdate(double delta) {
        editorState.ClearIfDestroyed(SceneManager.GetCurrentScene());
        editorInput.NewFrame();
        editorCamera.Update((float)delta, sceneViewHovered && !imgui.WantCaptureMouse, editorInput);
        bootstrap.UpdateFrame(delta); // scene ticks only while playing
    }

    void OnRender(double delta) {
        RenderSceneView();
        RenderGameView();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ClearColor(0.10f, 0.10f, 0.12f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        imgui.Update((float)delta);
        BuildUI();
        imgui.Render();
    }

    void RenderSceneView() {
        var w = Math.Max(1, (int)sceneViewSize.X);
        var h = Math.Max(1, (int)sceneViewSize.Y);
        if (w != sceneW || h != sceneH) { Renderer.ResizeSceneTarget(w, h); sceneW = w; sceneH = h; }
        editorCamera.SetAspect((float)w / h);

        Renderer.ActiveTarget = HDRenderer.RenderTarget.Scene;
        Renderer.BeginRender(new RendererArgs(editorCamera));
        Renderer.PostRenderCleanUp();
    }

    void RenderGameView() {
        if (SceneManager.RenderCamera is null)
            return; // nothing to show until a scene camera exists (e.g. during play)

        var w = Math.Max(1, (int)gameViewSize.X);
        var h = Math.Max(1, (int)gameViewSize.Y);
        if (w != gameW || h != gameH) { Renderer.ResizeGameTarget(w, h); gameW = w; gameH = h; }

        Renderer.ActiveTarget = HDRenderer.RenderTarget.Game;
        Renderer.BeginRender(new RendererArgs(SceneManager.RenderCamera));
        Renderer.PostRenderCleanUp();
    }

    void BuildUI() {
        ToolbarUI();
        SceneViewUI();
        GameViewUI();
        hierarchy.Draw();
        inspector.Draw();
        assets.Draw();
    }

    void ToolbarUI() {
        ImGui.SetNextWindowPos(new SysVec2(220, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new SysVec2(420, 70), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Toolbar")) {
            if (SceneManager.IsPlaying) {
                if (ImGui.Button("Stop")) { SceneManager.StopPlay(); editorState.Selected = null; }
            }
            else {
                if (ImGui.Button("Play")) SceneManager.StartPlay();
            }
            ImGui.SameLine();
            ImGui.Text(SceneManager.IsPlaying ? "Playing" : "Edit");
            ImGui.SameLine();
            if (ImGui.Button("Save")) SaveScene();
        }
        ImGui.End();
    }

    void SceneViewUI() {
        ImGui.SetNextWindowPos(new SysVec2(220, 90), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new SysVec2(760, 500), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, SysVec2.Zero);
        if (ImGui.Begin("Scene")) {
            sceneViewHovered = ImGui.IsWindowHovered();
            SysVec2 avail = ImGui.GetContentRegionAvail();
            if (avail.X > 0 && avail.Y > 0) sceneViewSize = avail;
            ImGui.Image(Renderer.SceneColorTextureId, sceneViewSize, new SysVec2(0, 1), new SysVec2(1, 0));
        }
        else sceneViewHovered = false;
        ImGui.End();
        ImGui.PopStyleVar();
    }

    void GameViewUI() {
        ImGui.SetNextWindowPos(new SysVec2(990, 90), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new SysVec2(380, 250), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, SysVec2.Zero);
        if (ImGui.Begin("Game")) {
            SysVec2 avail = ImGui.GetContentRegionAvail();
            if (avail.X > 0 && avail.Y > 0) gameViewSize = avail;

            if (SceneManager.RenderCamera is not null)
                ImGui.Image(Renderer.GameColorTextureId, gameViewSize, new SysVec2(0, 1), new SysVec2(1, 0));
            else
                ImGui.TextDisabled("No scene camera. Press Play, or add an HDCamera.");
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    void SaveScene() {
        var startup = bootstrap.Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(startup))
            return;
        SceneSerializer.Save(SceneManager.GetCurrentScene(), bootstrap.Project.ResolveAbsolute(startup));
    }
}
