using BallisticEngine.Serialization;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Drives the editor: a fixed, DPI-scaled tiled layout (toolbar, Hierarchy, Scene/Game tabs,
// Inspector, Assets) over the engine. The scene renders offscreen for whichever view tab is
// active. Engine input (component Tick + renderer debug keys) only flows while playing AND the
// Game view is focused, so editor panels never leak input into the game.
internal sealed class EditorApplication {
    const ImGuiWindowFlags PanelFlags =
        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings;

    readonly IBallisticEngineRuntime runtime;
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;
    readonly EditorCamera editorCamera = new();
    readonly EditorInput editorInput;
    readonly EditorState editorState = new();

    readonly HierarchyPanel hierarchy;
    readonly InspectorPanel inspector;
    readonly AssetBrowserPanel assets;
    readonly TransformGizmo gizmo = new();

    HDRenderer Renderer => RenderAsset.Current.Renderer;
    float S => imgui.Scale;

    SysVec2 sceneViewSize = new(1280, 720);
    SysVec2 gameViewSize = new(1280, 720);
    int sceneW, sceneH, gameW, gameH;
    bool sceneViewHovered;
    bool gameViewFocused;
    bool sceneTabActive = true;
    bool selectGameTab;
    bool selectSceneTab = true; // explicitly select Scene on the first frame

    public EditorApplication(GLBallisticEngineWindow window, string projectPath) {
        runtime = window;
        bootstrap = new EngineBootstrap(window, projectPath);
        imgui = new ImGuiController(window);
        editorInput = new EditorInput(window);
        hierarchy = new HierarchyPanel(editorState);
        inspector = new InspectorPanel(editorState);
        assets = new AssetBrowserPanel(editorState, () => imgui.Scale);

        bootstrap.LoadStartupScene();
        Renderer.PresentToScreen = false;

        // Files dragged from the OS onto the editor window import into the browser's folder.
        window.FileDrop += e => ImportDroppedFiles(e.FileNames);

        window.WindowState = WindowState.Maximized;
        window.OnResizeCallback += (w, h) => {
            imgui.WindowResized(w, h);
            sceneW = sceneH = gameW = gameH = 0; // re-sync offscreen targets next frame
        };
        imgui.WindowResized(window.Width, window.Height);

        runtime.Window.SetFrequency(0);
        runtime.WindowUpdateCallback += OnUpdate;
        runtime.WindowRenderCallback += OnRender;
    }

    void OnUpdate(double delta) {
        editorState.ClearIfDestroyed(SceneManager.GetCurrentScene());

        // Game/engine input flows only while playing with the Game view focused; editor panels
        // and the scene camera otherwise own all input (kills the leaking debug hotkeys too).
        Input.Enabled = SceneManager.IsPlaying && gameViewFocused;

        editorInput.NewFrame();
        editorCamera.Update((float)delta, sceneViewHovered && !imgui.WantTextInput, editorInput);
        bootstrap.UpdateFrame(delta);
    }

    void OnRender(double delta) {
        if (sceneTabActive)
            RenderSceneView();
        else
            RenderGameView();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ClearColor(0.05f, 0.05f, 0.06f, 1f);
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

    // The Game view works in edit mode too: it renders from the first active HDCamera in the
    // hierarchy (or the play-mode RenderCamera when playing).
    HDCamera FindSceneCamera() {
        if (SceneManager.RenderCamera is not null)
            return SceneManager.RenderCamera;

        foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
            if (!entity.IsActive)
                continue;
            HDCamera cam = entity.GetComponent<HDCamera>();
            if (cam is not null && cam.IsEnabled)
                return cam;
        }

        return null;
    }

    readonly SceneCameraView gameCameraView = new();

    void RenderGameView() {
        HDCamera camera = FindSceneCamera();
        if (camera is null)
            return;

        var w = Math.Max(1, (int)gameViewSize.X);
        var h = Math.Max(1, (int)gameViewSize.Y);
        if (w != gameW || h != gameH) { Renderer.ResizeGameTarget(w, h); gameW = w; gameH = h; }

        gameCameraView.Bind(camera, (float)w / h);
        Renderer.ActiveTarget = HDRenderer.RenderTarget.Game;
        Renderer.BeginRender(new RendererArgs(gameCameraView));
        Renderer.PostRenderCleanUp();
    }

    // ---- Layout -------------------------------------------------------------

    void BuildUI() {
        SysVec2 display = ImGui.GetIO().DisplaySize;
        float toolbarH = 44 * S;
        float leftW = Math.Clamp(display.X * 0.14f, 220 * S, 340 * S);
        float rightW = Math.Clamp(display.X * 0.18f, 300 * S, 440 * S);
        float assetsH = Math.Clamp((display.Y - toolbarH) * 0.26f, 160 * S, 420 * S);
        float centerW = display.X - leftW - rightW;
        float centerH = display.Y - toolbarH - assetsH;

        Panel("##toolbar", new SysVec2(0, 0), new SysVec2(display.X, toolbarH),
            PanelFlags | ImGuiWindowFlags.NoTitleBar, ToolbarUI);

        Panel("Hierarchy", new SysVec2(0, toolbarH), new SysVec2(leftW, display.Y - toolbarH - assetsH),
            PanelFlags, hierarchy.DrawContents);

        ViewportPanel(new SysVec2(leftW, toolbarH), new SysVec2(centerW, centerH));

        Panel("Inspector", new SysVec2(display.X - rightW, toolbarH), new SysVec2(rightW, display.Y - toolbarH),
            PanelFlags, inspector.DrawContents);

        Panel("Assets", new SysVec2(0, display.Y - assetsH), new SysVec2(display.X - rightW, assetsH),
            PanelFlags, assets.DrawContents);
    }

    static void Panel(string name, SysVec2 pos, SysVec2 size, ImGuiWindowFlags flags, Action contents) {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (ImGui.Begin(name, flags))
            contents();
        ImGui.End();
    }

    // ---- Toolbar ------------------------------------------------------------

    void ToolbarUI() {
        Scene scene = SceneManager.GetCurrentScene();

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(bootstrap.Project.Manifest.Name);
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.Text(scene.Name);

        ImGui.SameLine(0, 24 * S);
        GizmoModeButton("Move", GizmoMode.Translate);
        ImGui.SameLine();
        GizmoModeButton("Rotate", GizmoMode.Rotate);
        ImGui.SameLine();
        GizmoModeButton("Scale", GizmoMode.Scale);

        // Center the Play/Stop control.
        float buttonW = 84 * S;
        ImGui.SameLine((ImGui.GetWindowWidth() - buttonW) * 0.5f);

        if (SceneManager.IsPlaying) {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.65f, 0.27f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.78f, 0.33f, 0.22f, 1f));
            if (ImGui.Button("Stop", new SysVec2(buttonW, 0))) {
                SceneManager.StopPlay();
                editorState.Selected = null;
                selectSceneTab = true;
            }
            ImGui.PopStyleColor(2);
        }
        else {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.18f, 0.45f, 0.25f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.22f, 0.56f, 0.31f, 1f));
            if (ImGui.Button("Play", new SysVec2(buttonW, 0))) {
                SceneManager.StartPlay();
                selectGameTab = true;
            }
            ImGui.PopStyleColor(2);
        }

        // Right side: Save + FPS.
        float rightBlock = 170 * S;
        ImGui.SameLine(ImGui.GetWindowWidth() - rightBlock);
        if (ImGui.Button("Save", new SysVec2(64 * S, 0)))
            SaveScene();
        ImGui.SameLine();
        ImGui.TextDisabled($"{runtime.Window.FrameRate} fps");
    }

    // ---- Viewport (Scene / Game tabs) ----------------------------------------

    void ViewportPanel(SysVec2 pos, SysVec2 size) {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(1, 1));
        if (ImGui.Begin("##viewport", PanelFlags | ImGuiWindowFlags.NoTitleBar)) {
            if (ImGui.BeginTabBar("##viewtabs")) {
                if (BeginTab("Scene", selectSceneTab)) {
                    sceneTabActive = true;
                    SceneTabContents();
                    ImGui.EndTabItem();
                }

                if (BeginTab("Game", selectGameTab)) {
                    sceneTabActive = false;
                    GameTabContents();
                    ImGui.EndTabItem();
                }

                selectSceneTab = selectGameTab = false;
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    void SceneTabContents() {
        SysVec2 avail = ImGui.GetContentRegionAvail();
        if (avail.X > 0 && avail.Y > 0) sceneViewSize = avail;

        ImGui.Image(Renderer.SceneColorTextureId, sceneViewSize, new SysVec2(0, 1), new SysVec2(1, 0));
        SysVec2 imageMin = ImGui.GetItemRectMin();
        sceneViewHovered = ImGui.IsItemHovered();
        gameViewFocused = false;

        // W/E/R switch gizmo mode (only when not flying the camera, which also uses WASD).
        if (sceneViewHovered && !editorInput.RightMouseDown) {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) gizmo.Mode = GizmoMode.Translate;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) gizmo.Mode = GizmoMode.Rotate;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) gizmo.Mode = GizmoMode.Scale;
        }

        if (editorState.Selected is not null)
            gizmo.Draw(editorCamera, editorState.Selected, imageMin, sceneViewSize, sceneViewHovered);
    }

    void GameTabContents() {
        SysVec2 avail = ImGui.GetContentRegionAvail();
        if (avail.X > 0 && avail.Y > 0) gameViewSize = avail;

        sceneViewHovered = false;

        if (FindSceneCamera() is not null) {
            ImGui.Image(Renderer.GameColorTextureId, gameViewSize, new SysVec2(0, 1), new SysVec2(1, 0));
            gameViewFocused = ImGui.IsWindowFocused();
        }
        else {
            ImGui.Dummy(new SysVec2(0, avail.Y * 0.45f));
            CenteredText("No camera in the scene. Add an HDCamera component.");
            gameViewFocused = false;
        }
    }

    static void CenteredText(string text) {
        float w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) * 0.5f);
        ImGui.TextDisabled(text);
    }

    // Copies OS-dropped files into the browser's current folder and runs the import pipeline —
    // each file's dedicated importer (model/texture/Falcor/...) picks it up in the refresh.
    void ImportDroppedFiles(IReadOnlyList<string> files) {
        var destFolder = Path.Combine(bootstrap.Project.RootPath,
            assets.CurrentFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destFolder);

        var copied = 0;
        foreach (var source in files) {
            if (!File.Exists(source)) {
                Debugging.LogWarning($"Drop import: '{source}' is not a file (folders not supported yet).");
                continue;
            }

            var destination = Path.Combine(destFolder, Path.GetFileName(source));
            if (File.Exists(destination)) {
                Debugging.LogWarning($"Drop import: '{Path.GetFileName(source)}' already exists in {assets.CurrentFolder}; skipped.");
                continue;
            }

            File.Copy(source, destination);
            copied++;
        }

        if (copied > 0)
            AssetDatabase.Refresh();
    }

    // ImGui.NET only exposes tab-item flags on the closable overload; pass a pinned `true` and
    // the SetSelected flag only on the single frame a programmatic switch is requested.
    static bool pinnedOpen = true;

    static bool BeginTab(string label, bool forceSelect) {
        if (!forceSelect)
            return ImGui.BeginTabItem(label);

        pinnedOpen = true;
        return ImGui.BeginTabItem(label, ref pinnedOpen, ImGuiTabItemFlags.SetSelected);
    }

    void GizmoModeButton(string label, GizmoMode mode) {
        var active = gizmo.Mode == mode;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.17f, 0.36f, 0.53f, 1f));
        if (ImGui.Button(label, new SysVec2(64 * S, 0)))
            gizmo.Mode = mode;
        if (active)
            ImGui.PopStyleColor();
    }

    void SaveScene() {
        var startup = bootstrap.Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(startup))
            return;
        SceneSerializer.Save(SceneManager.GetCurrentScene(), bootstrap.Project.ResolveAbsolute(startup));
    }
}
