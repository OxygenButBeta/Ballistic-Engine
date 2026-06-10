using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Drives the editor: brings the engine up, renders the scene into an offscreen target with a free
// editor camera, and presents it inside an ImGui "Viewport" window. Play/Stop toggles ticking.
internal sealed class EditorApplication {
    readonly IBallisticEngineRuntime runtime;
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;
    readonly EditorCamera editorCamera = new();
    readonly EditorInput editorInput;

    HDRenderer Renderer => RenderAsset.Current.Renderer;

    SysVec2 viewportSize = new(1280, 720);
    int targetWidth, targetHeight;
    bool viewportHovered;

    public EditorApplication(GLBallisticEngineWindow window, string projectPath) {
        runtime = window;
        bootstrap = new EngineBootstrap(window, projectPath);
        imgui = new ImGuiController(window);
        editorInput = new EditorInput(window);

        bootstrap.LoadStartupScene();

        // Editor owns presentation: the engine renders into its offscreen target, we sample it.
        Renderer.PresentToScreen = false;

        window.OnResizeCallback += (w, h) => imgui.WindowResized(w, h);
        imgui.WindowResized(window.Width, window.Height);

        runtime.Window.SetFrequency(0);
        runtime.WindowUpdateCallback += OnUpdate;
        runtime.WindowRenderCallback += OnRender;
    }

    void OnUpdate(double delta) {
        editorInput.NewFrame();
        editorCamera.Update((float)delta, viewportHovered && !imgui.WantCaptureMouse, editorInput);
        bootstrap.UpdateFrame(delta); // scene ticks only while playing
    }

    void OnRender(double delta) {
        RenderScene();

        // Draw ImGui to the default framebuffer.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ClearColor(0.10f, 0.10f, 0.12f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        imgui.Update((float)delta);
        BuildUI();
        imgui.Render();
    }

    void RenderScene() {
        var w = Math.Max(1, (int)viewportSize.X);
        var h = Math.Max(1, (int)viewportSize.Y);

        // Only recreate the offscreen target when the panel actually changes size — resizing
        // every frame deletes/regenerates the color texture and makes the viewport flicker.
        if (w != targetWidth || h != targetHeight) {
            Renderer.ResizeSceneTarget(w, h);
            targetWidth = w;
            targetHeight = h;
        }

        editorCamera.SetAspect((float)w / h);

        // In edit mode there is no scene RenderCamera; drive the renderer with the editor camera.
        // In play mode the scene camera is authoritative if present.
        IViewProjectionProvider view = SceneManager.IsPlaying && SceneManager.RenderCamera is not null
            ? SceneManager.RenderCamera
            : editorCamera;

        Renderer.BeginRender(new RendererArgs(view));
        Renderer.PostRenderCleanUp();
    }

    void BuildUI() {
        ToolbarUI();
        ViewportUI();
    }

    void ToolbarUI() {
        ImGui.SetNextWindowSize(new SysVec2(360, 90), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Toolbar")) {
            if (SceneManager.IsPlaying) {
                if (ImGui.Button("Stop")) SceneManager.StopPlay();
            }
            else {
                if (ImGui.Button("Play")) SceneManager.StartPlay();
            }
            ImGui.SameLine();
            ImGui.Text(SceneManager.IsPlaying ? "Playing" : "Edit");
            ImGui.SameLine();
            ImGui.Text($"| {SceneManager.GetCurrentScene().Entities.Count} entities");
        }
        ImGui.End();
    }

    void ViewportUI() {
        ImGui.SetNextWindowSize(new SysVec2(960, 600), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, SysVec2.Zero);
        if (ImGui.Begin("Viewport")) {
            viewportHovered = ImGui.IsWindowHovered();
            SysVec2 avail = ImGui.GetContentRegionAvail();
            if (avail.X > 0 && avail.Y > 0)
                viewportSize = avail;

            // Flip V: GL textures are bottom-up.
            ImGui.Image(Renderer.SceneColorTextureId, viewportSize, new SysVec2(0, 1), new SysVec2(1, 0));
        }
        else {
            viewportHovered = false;
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }
}
