using BallisticEngine.Serialization;
using Hexa.NET.ImGui;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
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
    readonly GLBallisticEngineWindow window;
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;
    readonly EditorCamera editorCamera = new();
    readonly EditorInput editorInput;
    readonly EditorState editorState = new();

    readonly HierarchyPanel hierarchy;
    readonly InspectorPanel inspector;
    readonly AssetBrowserPanel assets;
    readonly ConsolePanel console = new();
    readonly StatsPanel stats = new();
    readonly SettingsPanel settings;
    readonly TagsLayersPanel tagsLayers = new();
    readonly ProfilerPanel profilerPanel = new();
    readonly BuildPanel buildPanel;
    readonly EditorProfilerBackend profiler;
    readonly TransformGizmo gizmo = new();
    readonly GizmoDrawer gizmoDrawer = new();

    bool showGizmos = EditorPrefs.Current.ShowGizmos;  // component gizmos in the Scene view

    // Panel visibility (toggled from the Window menu / each window's close button). Scene/Game and
    // Entities/Scene-components are independent dockable windows (default-tabbed together).
    bool showHierarchy = true;        // the Entities window
    bool showSceneComponents = true;  // the Scene-components window
    bool showInspector = true;
    bool showBottom = true;     // the Assets window
    bool showConsole = true;    // the Console window
    // Double-click ANY panel's tab to fill the window with it; Esc restores. null = no panel
    // maximized. (Was viewport-only; now works for every dockable panel.)
    string maximizedPanel;
    bool maximizedViewport => maximizedPanel == EditorLayout.SceneView || maximizedPanel == EditorLayout.GameView;

    bool showStats;
    bool alwaysRefresh = EditorPrefs.Current.AlwaysRefresh;   // off = re-render only on change
    int forceFrames = 3;
    Matrix4 lastCameraMatrix = Matrix4.Identity;   // previous frame's editor-camera pose (idle-render trigger)
    SysVec2 pickPressPos;        // where LMB went down in the viewport (click-vs-drag test for picking)
    bool pickPressValid;         // the press began as a candidate select-click (not on a gizmo/handle)
    float editorCpuMs;
    readonly System.Diagnostics.Stopwatch frameWatch = new();

    HDRenderer Renderer => RenderAsset.Current.Renderer;
    float S => imgui.Scale;

    // GL texture name -> ImGui texture handle. Hexa's ImGui.Image/ImageButton take an ImTextureID
    // (u64 handle), with no implicit int conversion, so every raw GL texture id routes through here.
    internal static ImTextureID Tex(int glTextureId) => new((ulong)glTextureId);

    SysVec2 sceneViewSize = new(1280, 720);   // render resolution of the Scene offscreen target
    SysVec2 gameViewSize = new(1280, 720);     // render resolution of the Game offscreen target
    SysVec2 scenePanelSize = new(1280, 720);   // on-screen panel area available for the Scene view
    SysVec2 gamePanelSize = new(1280, 720);
    readonly ViewportResolution sceneRes = new();
    readonly ViewportResolution gameRes = new();
    int sceneW, sceneH, gameW, gameH;
    bool sceneViewHovered;
    bool gameViewFocused;
    bool gameViewHovered;   // mouse is over the Game view image (used to gate click-to-recapture)
    bool sceneTabActive = true;
    // A window name to focus next frame (play → Game View, stop → Scene View). Now that Scene/Game are
    // separate dockable windows, "select tab" means focus that window so it surfaces above its dock node.
    string pendingFocusWindow = EditorLayout.SceneView;

    public EditorApplication(GLBallisticEngineWindow window, string projectPath) {
        runtime = window;

        // Record every main-thread zone for the Profiler panel, forwarding to Tracy if
        // Program.cs installed it (BALLISTIC_TRACY=1).
        profiler = new EditorProfilerBackend(Profiler.Backend);
        Profiler.Backend = profiler;

        // Defer the (slow) asset import: bring the window up first, then refresh asynchronously behind
        // the busy overlay. The startup scene loads once that first import completes (see OnRender).
        bootstrap = new EngineBootstrap(window, projectPath, deferAssetRefresh: true);

        // The editor consumes runtime debug lines (Debug.DrawLine/DrawRay) via the gizmo drawer;
        // turning this on makes the engine-side buffer actually record (a shipped player leaves it
        // off so release play pays nothing).
        DebugDraw.Enabled = true;

        imgui = new ImGuiController(window);
        editorInput = new EditorInput(window);
        hierarchy = new HierarchyPanel(editorState);
        inspector = new InspectorPanel(editorState);
        assets = new AssetBrowserPanel(editorState, () => imgui.Scale);
        assets.RequestScriptRebuild = RebuildScripts;
        hierarchy.CurrentAssetFolder = () => assets.CurrentFolder;
        settings = new SettingsPanel(imgui.SetAccent, ApplyFrameRateLimit);
        buildPanel = new BuildPanel(bootstrap.Project);

        // Per-project dock layout: key by the project root, then apply the saved arrangement before the
        // first frame (BuildUI lays out the default if none exists).
        EditorLayout.SetProject(bootstrap.Project.RootPath);
        EditorLayout.Load();

        // Restore the Scene-view camera to wherever it was last left in this project.
        editorCamera.RestorePose(EditorPrefs.GetLastCamera(bootstrap.Project.RootPath));

        Renderer.PresentToScreen = false;

        // Files dragged from the OS onto the editor window import into the browser's folder.
        window.FileDrop += e => ImportDroppedFiles(e.FileNames);

        // Unity-style auto-compile: regaining window focus (back from the IDE after editing a
        // script) re-checks the sources on the next update tick. The up-to-date fast path in
        // GameScripts makes this a cheap mtime scan when nothing changed.
        window.FocusedChanged += e => {
            if (e.IsFocused)
                scriptsRecheckPending = true;
        };

        window.WindowState = WindowState.Maximized;
        window.OnResizeCallback += (w, h) => {
            imgui.WindowResized(w, h);
            sceneW = sceneH = gameW = gameH = 0; // re-sync offscreen targets next frame
        };
        imgui.WindowResized(window.Width, window.Height);

        this.window = window;
        ApplyFrameRateLimit();

        // Keep the selection alive across undo/redo: the scene is rebuilt from YAML on Restore, so
        // EditorUndo captures a stable token before and re-selects the equivalent live object after
        // (entity InstanceIds round-trip through the scene file; see SceneSerializer / BObject).
        EditorUndo.CaptureSelection = CaptureSelectionToken;
        EditorUndo.RestoreSelection = t => RestoreSelectionToken(t as SelectionToken);

        runtime.WindowUpdateCallback += OnUpdate;
        runtime.WindowRenderCallback += OnRender;

        // Remote command port (agents/MCP): a named-pipe server whose commands run on the main
        // thread via RemoteCommandQueue.Pump() in OnRender. Engine-owned thread — survives script
        // hot-reload and play transitions.
        RemotePort.Start(editorState, bootstrap);
    }

    // A selection that can survive a scene rebuild: an entity by its (round-tripped) InstanceId, or a
    // scene behaviour by type + ordinal among same-type behaviours (no per-component id is persisted).
    sealed class SelectionToken {
        public Guid? EntityId;
        public Type SceneBehaviourType;
        public int SceneBehaviourIndex;
    }

    object CaptureSelectionToken() {
        if (editorState.Selected is { } e)
            return new SelectionToken { EntityId = e.InstanceId };

        if (editorState.SelectedSceneBehaviour is { } sb) {
            Scene scene = SceneManager.GetCurrentScene();
            Type type = sb.GetType();
            var index = 0;
            foreach (SceneBehaviour b in scene.SceneBehaviours) {
                if (ReferenceEquals(b, sb)) break;
                if (b.GetType() == type) index++;
            }
            return new SelectionToken { SceneBehaviourType = type, SceneBehaviourIndex = index };
        }

        return null;
    }

    void RestoreSelectionToken(SelectionToken token) {
        if (token is null)
            return;

        Scene scene = SceneManager.GetCurrentScene();

        if (token.EntityId is { } id) {
            foreach (Entity e in scene.Entities)
                if (e.InstanceId == id) { editorState.Select(e); return; }
            return; // entity no longer exists in this state (e.g. undid its creation)
        }

        if (token.SceneBehaviourType is not null) {
            var index = 0;
            foreach (SceneBehaviour b in scene.SceneBehaviours) {
                if (b.GetType() != token.SceneBehaviourType) continue;
                if (index++ == token.SceneBehaviourIndex) { editorState.SelectSceneBehaviour(b); return; }
            }
        }
    }

    void OnUpdate(double delta) {
        // Start the frame by expiring last frame's debug lines: single-frame segments drop, timed
        // ones survive until their duration elapses. Then this frame's Tick (in UpdateFrame below)
        // repopulates and OnRender drains them via DrawComponentGizmos â€” Unity's ordering.
        DebugDraw.Expire();

        // Re-detect monitor DPI in case the window moved to a different-scale display (4K <-> 1080p).
        // No-ops unless the scale actually changed; must run before ImGui.NewFrame (it's in OnRender).
        imgui.RefreshScale();

        editorState.ClearIfDestroyed(SceneManager.GetCurrentScene());

        // Focus-regain rechecks, deferred here so they never run inside the OS event callback.
        // Scripts first (synchronous; would no-op if an import were already running), then the
        // asset database when files changed EXTERNALLY (IDE renames, Explorer copies) so the
        // browser reflects them. Reload is LIVE during play: code swaps under the running game,
        // serializable state preserved (ReloadGameScripts) â€” play does not stop.
        if (scriptsRecheckPending) {
            scriptsRecheckPending = false;
            RebuildScripts();
            if (!AsyncAssetImport.IsBusy && AssetChangeWatch.ChangedExternally())
                AsyncAssetImport.Request("Refreshing assets...", onFinished: assets.InvalidateThumbnails);
        }

        // Game/engine input flows only while playing with the Game view focused; editor panels
        // and the scene camera otherwise own all input (kills the leaking debug hotkeys too).
        // A background import also locks input out â€” the busy overlay owns the screen.
        Input.Enabled = SceneManager.IsPlaying && gameViewFocused && !AsyncAssetImport.IsBusy;

        // The pointer counts as "in the game" while it's over the Game view image, OR whenever the
        // cursor is already locked (then it's pinned to the game's centre). A script gates click-to-
        // recapture on this, so clicking the Inspector â€” pointer NOT over the game image â€” never grabs
        // the cursor back. (The standalone player leaves PointerInGameView at its default true.)
        Input.PointerInGameView = gameViewHovered || window.CursorMode == CursorMode.Locked;

        editorInput.NewFrame();
        var allowCameraInput = sceneViewHovered && !imgui.WantTextInput && !AsyncAssetImport.IsBusy;
        editorCamera.Update((float)delta, allowCameraInput, editorInput);
        MaybeSaveCameraPose((float)delta);

        HandleGlobalShortcuts();

        bootstrap.UpdateFrame(delta); // component Tick runs here; a player script sets its Cursor intent

        // The editor is the SOLE cursor writer (no fighting/flicker with the script). Resolve the
        // script's cursor intent onto the window only while game input is actually live â€” playing AND
        // the Game tab is the focused surface. Any other state (Scene tab, a panel focused, paused,
        // importing) vetoes it to Normal, so the cursor is grabbed ONLY in the Game view. Esc inside
        // the game sets intent=Normal via the script; clicking back into the Game view re-locks.
        Cursor.Apply(allowed: Input.Enabled);
    }

    // Global Ctrl shortcuts handled from RAW OpenTK input (not ImGui), so undo/redo/save fire no
    // matter which panel has focus â€” only suppressed while typing in a text field.
    void HandleGlobalShortcuts() {
        if (!editorInput.CtrlDown || imgui.WantTextInput)
            return;

        if (editorInput.KeyPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Z)) {
            EditorUndo.Undo();
            MarkSceneDirty();
        }
        if (editorInput.KeyPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Y)) {
            EditorUndo.Redo();
            MarkSceneDirty();
        }
        if (editorInput.KeyPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.S))
            SaveScene();
        if (editorInput.KeyPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.R))
            RebuildScripts();
    }

    bool startupImportKicked;
    bool scriptsRecheckPending;

    // Persist the Scene-view camera pose periodically so it survives a crash/close without a dedicated
    // exit hook. Throttled (~1.5s) and only writes when the pose actually changed, to avoid disk churn.
    float cameraSaveTimer;
    string lastSavedCameraPose;

    void MaybeSaveCameraPose(float delta) {
        cameraSaveTimer += delta;
        if (cameraSaveTimer < 1.5f)
            return;
        cameraSaveTimer = 0f;

        string pose = editorCamera.SerializePose();
        if (pose == lastSavedCameraPose)
            return;
        lastSavedCameraPose = pose;
        EditorPrefs.SetLastCamera(bootstrap.Project.RootPath, pose);
        EditorPrefs.Save();
    }

    void OnRender(double delta) {
        frameWatch.Restart();

        // Run any main-thread completion work from a finished background import (thumbnail/asset
        // cache invalidation) before building this frame's UI off the fresh asset database.
        AsyncAssetImport.PumpCompletion();

        // Remote commands (agents/MCP) execute here — on the main thread, before the UI builds,
        // so a remote edit and a human edit are indistinguishable to the rest of the frame.
        RemoteCommandQueue.Pump();

        // Build the UI FIRST (the gizmo mutates transforms there), then render the scene with
        // this frame's values â€” otherwise the object trails the gizmo by one frame.
        using (Profiler.Zone("Editor.BuildUI")) {
            imgui.Update((float)delta);
            BuildUI();
            BusyOverlay.Draw(S);
        }

        // Kick the startup asset import on the first painted frame (not in the constructor), so the
        // window and the busy overlay are already on screen instead of a black, frozen window. The
        // startup scene loads on the render thread once the import finishes.
        if (!startupImportKicked) {
            startupImportKicked = true;
            AsyncAssetImport.Request("Importing project assets...", onFinished: LoadStartupScene);
        }

        // "Always refresh" off: re-render the scene only while something is changing
        // (playing, flying, gizmo drag, recent interaction). The last image stays on screen.
        // Skip the scene render while a deferred open is pending â€” the scene is about to be replaced.
        // A probe bake counts as "changing": its time-sliced job only advances inside the scene
        // render, so without this it crawls one slice per click instead of one per frame.
        var probeBakePending = IrradianceVolume.IsBaking ||
                               IrradianceVolume.Active is { IsActive: true, Bake: true };
        // A panel edit that changes the scene's appearance (light toggle, entity disable, component
        // value, add/remove component) flags the viewport dirty; pick that up here so the on-demand
        // renderer paints the change instead of leaving the previous frame frozen. IsAnyItemActive
        // covers in-progress drags (sliders/color pickers) so they update live while held.
        if (editorState.ConsumeViewportDirty() || ImGui.IsAnyItemActive())
            MarkSceneDirty();

        // Force a repaint whenever the editor camera moved since the last frame, no matter what moved
        // it (fly-cam, F-to-frame, Ctrl+Shift+F, orientation cube). Without this, one-shot camera jumps
        // leave a stale frame frozen on screen with AlwaysRefresh off â€” the view looks broken until the
        // next interaction. Cheap: one matrix compare per frame.
        Matrix4 camMatrix = editorCamera.Transform.WorldMatrix;
        if (camMatrix != lastCameraMatrix) {
            lastCameraMatrix = camMatrix;
            MarkSceneDirty();
        }
        // A live game UIDocument animates per frame (tweens, pulses, loading), so the Game view must
        // keep repainting while one is active — otherwise on-demand rendering freezes the UI after the
        // initial forceFrames run out (it builds in the controller's OnAttach but never draws again).
        bool activeGameUI = !sceneTabActive && BallisticEngine.UI.UIDocument.Active.Count > 0;
        var renderScene = !SceneCommands.IsLoading &&
                          (alwaysRefresh || SceneManager.IsPlaying || editorInput.RightMouseDown ||
                           gizmo.IsInteracting || forceFrames > 0 || probeBakePending || activeGameUI);
        if (renderScene) {
            using var profileZone = Profiler.Zone("Editor.SceneRender");
            if (sceneTabActive)
                RenderSceneView();
            else
                RenderGameView();
            if (forceFrames > 0)
                forceFrames--;
        }

        // After the scene has rendered: resume `await Coroutine.EndOfFrame()` continuations (only
        // does anything while playing; the runner is empty otherwise).
        if (SceneManager.IsPlaying)
            Coroutine.EndOfFramePump();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.ClearColor(0.05f, 0.05f, 0.06f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        using (Profiler.Zone("Editor.ImGuiRender"))
            imgui.Render();

        // Pump the deferred scene open. NOTE: the buffer swap happens after OnRender returns, so a
        // blocking apply here stalls BEFORE this frame presents â€” SceneCommands defers the apply two
        // frames after prefetch so its final status is actually on screen. Refresh thumbnails after.
        if (SceneCommands.PumpPendingOpen()) {
            assets.InvalidateThumbnails();
            pendingFocusWindow = EditorLayout.SceneView;
            MarkSceneDirty();
        }

        // Exponential moving average so the value is readable.
        editorCpuMs = editorCpuMs * 0.9f + (float)frameWatch.Elapsed.TotalMilliseconds * 0.1f;

        // IDLE THROTTLE: when nothing is happening — not playing, no scene render, no mouse/keyboard
        // activity, no open popup — there's no point spinning ImGui at hundreds of FPS (wasted CPU/GPU/
        // battery for an identical frame). Drop to a low idle cap; snap back to full the instant the
        // user does anything. Skipped when the user picked an explicit FPS cap below the idle rate.
        UpdateIdleThrottle(renderScene, delta);
    }

    // ---- Idle frame throttle -------------------------------------------------
    const int IdleFps = 30;          // frame cap while the editor is idle
    double idleSeconds;              // time since the last activity (0 = active)

    void UpdateIdleThrottle(bool renderedScene, double delta) {
        ImGuiIOPtr io = ImGui.GetIO();
        bool active = renderedScene || SceneManager.IsPlaying ||
                      io.WantTextInput ||                         // typing in a field
                      io.MouseDown[0] || io.MouseDown[1] || io.MouseDown[2] || // any mouse button held
                      Math.Abs(io.MouseDelta.X) > 0.1f || Math.Abs(io.MouseDelta.Y) > 0.1f || // mouse moving
                      io.MouseWheel != 0f ||                      // scrolling
                      ImGui.IsAnyItemActive() ||                  // dragging a slider, etc.
                      ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);

        idleSeconds = active ? 0 : idleSeconds + delta;

        // A short grace period after activity keeps interactions smooth (tooltips, hover fades) before
        // dropping to the idle cap. The user's explicit cap still wins if it's already lower.
        int userCap = EditorPrefs.Current.FrameRateLimit;
        bool throttle = idleSeconds > 0.4 && (userCap <= 0 || userCap > IdleFps);
        double targetFreq = throttle ? IdleFps : (userCap <= 0 ? 0 : userCap);
        if (Math.Abs(window.UpdateFrequency - targetFreq) > 0.5) {
            window.UpdateFrequency = targetFreq;
            // VSync must be off for a positive cap to take effect (matches ApplyFrameRateLimit).
            window.VSync = targetFreq > 0
                ? OpenTK.Windowing.Common.VSyncMode.Off
                : OpenTK.Windowing.Common.VSyncMode.Adaptive;
        }
    }

    void MarkSceneDirty() => forceFrames = 3;

    // Applies the frame-rate limit from EditorPrefs. 0 = Adaptive VSync (lowest latency while we
    // keep up). A positive value disables VSync and caps the render/update loop to that FPS.
    public void ApplyFrameRateLimit() {
        int limit = EditorPrefs.Current.FrameRateLimit;
        if (limit <= 0) {
            window.VSync = OpenTK.Windowing.Common.VSyncMode.Adaptive;
            window.UpdateFrequency = 0;   // uncapped; VSync paces it
        }
        else {
            window.VSync = OpenTK.Windowing.Common.VSyncMode.Off;
            window.UpdateFrequency = limit;
        }
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

    bool layoutInitialized;
    bool resetLayoutRequested;

    void BuildUI() {
        ImGuiIOPtr io = ImGui.GetIO();

        if (maximizedPanel is not null && ImGui.IsKeyPressed(ImGuiKey.Escape))
            maximizedPanel = null;

        // Any interaction is a "scene might have changed" signal for the always-refresh-off mode.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right) ||
            ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || io.MouseWheel != 0 || ImGui.IsAnyItemActive())
            MarkSceneDirty();

        float menuH = DrawMainMenuBar();

        ImGuiViewportPtr vp = ImGui.GetMainViewport();
        float toolbarH = 44 * S;
        SysVec2 workPos = vp.WorkPos;
        SysVec2 workSize = vp.WorkSize;

        // Fullscreen: only the toolbar (for Play/Stop) + the maximized panel, nothing else (no docking).
        if (maximizedPanel is not null) {
            Panel("##toolbar", workPos, new SysVec2(workSize.X, toolbarH),
                PanelFlags | ImGuiWindowFlags.NoTitleBar, ToolbarUI);
            SysVec2 maxPos = workPos + new SysVec2(0, toolbarH);
            SysVec2 maxSize = new(workSize.X, workSize.Y - toolbarH);
            if (maximizedViewport)
                DrawMaximizedViewport(maxPos, maxSize);
            else
                DrawMaximizedPanel(maximizedPanel, maxPos, maxSize);
            settings.Draw(S);
            profilerPanel.Draw(profiler, S);
            buildPanel.Draw(S);
            DrawUnsavedPrompt();
            return;
        }

        // Fixed toolbar strip pinned under the menu bar (not dockable).
        Panel("##toolbar", workPos, new SysVec2(workSize.X, toolbarH),
            PanelFlags | ImGuiWindowFlags.NoTitleBar, ToolbarUI);

        // Full-window host window owning the central DockSpace. Transparent + chromeless so the docked
        // panels read as the whole editor; sits below the toolbar strip.
        SysVec2 hostPos = workPos + new SysVec2(0, toolbarH);
        SysVec2 hostSize = new(workSize.X, workSize.Y - toolbarH);
        ImGui.SetNextWindowPos(hostPos);
        ImGui.SetNextWindowSize(hostSize);
        ImGui.SetNextWindowViewport(vp.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, SysVec2.Zero);
        const ImGuiWindowFlags hostFlags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        ImGui.Begin("##DockHost", hostFlags);
        ImGui.PopStyleVar(3);

        uint dockId = ImGui.GetID("##MainDockSpace");
        ImGui.DockSpace(dockId, SysVec2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        // First run for this project with no saved layout (or an explicit Reset) builds the default.
        if (!layoutInitialized) {
            layoutInitialized = true;
            if (!EditorLayout.HasSaved)
                EditorLayout.BuildDefault(dockId, hostSize);
        }
        if (resetLayoutRequested) {
            resetLayoutRequested = false;
            EditorLayout.BuildDefault(dockId, hostSize);
        }
        ImGui.End();

        // Dockable panels — normal windows ImGui places into the dock tree. The Window-menu bools
        // double as each window's close-button state (passed by ref to Begin). Entities and Scene-
        // components are now separate dockable windows (were inner Hierarchy tabs).
        if (showHierarchy && ImGui.Begin(EditorLayout.Entities, ref showHierarchy)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.Entities);
            hierarchy.DrawEntitiesContents();
        }
        if (showHierarchy) ImGui.End();

        if (showSceneComponents && ImGui.Begin(EditorLayout.SceneComponents, ref showSceneComponents)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.SceneComponents);
            hierarchy.DrawSceneContents();
        }
        if (showSceneComponents) ImGui.End();

        if (showInspector && ImGui.Begin(EditorLayout.Inspector, ref showInspector)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.Inspector);
            inspector.DrawContents();
        }
        if (showInspector) ImGui.End();

        if (showBottom && ImGui.Begin(EditorLayout.Assets, ref showBottom)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.Assets);
            assets.DrawContents();
        }
        if (showBottom) ImGui.End();

        if (showConsole && ImGui.Begin(EditorLayout.Console, ref showConsole)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.Console);
            console.DrawContents();
        }
        if (showConsole) ImGui.End();

        // Scene + Game are separate dockable windows (were inner viewport tabs).
        DrawViewportWindows();

        settings.Draw(S);
        tagsLayers.Draw(S);
        profilerPanel.Draw(profiler, S);
        buildPanel.Draw(S);
        DrawUnsavedPrompt();

        // Persist the layout whenever ImGui says it changed (drag/dock/resize/tab).
        if (io.WantSaveIniSettings) {
            EditorLayout.Save();
            io.WantSaveIniSettings = false;
        }
    }

    // ---- Menu bar -----------------------------------------------------------

    // The pending action to run once the user resolves the unsaved-changes prompt (null = no prompt).
    Action pendingAfterSavePrompt;

    float DrawMainMenuBar() {
        float height = 0;
        if (!ImGui.BeginMainMenuBar())
            return height;

        height = ImGui.GetWindowSize().Y;

        if (ImGui.BeginMenu("File")) {
            if (ImGui.MenuItem($"{EditorIcons.Add}  New Scene", "Ctrl+N")) GuardUnsaved(SceneCommands.New);
            if (ImGui.MenuItem($"{EditorIcons.Save}  Save", "Ctrl+S")) SaveScene();
            ImGui.Separator();
            if (ImGui.MenuItem($"{EditorIcons.Refresh}  Rebuild Scripts", "Ctrl+R")) RebuildScripts();
            ImGui.Separator();
            if (ImGui.MenuItem($"{EditorIcons.Package}  Build...")) buildPanel.Open = true;
            ImGui.Separator();
            if (ImGui.MenuItem($"{EditorIcons.Cancel}  Exit")) GuardUnsaved(() => runtime.Window.Close());
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit")) {
            if (ImGui.MenuItem($"{EditorIcons.Undo}  Undo", "Ctrl+Z")) { EditorUndo.Undo(); MarkSceneDirty(); }
            if (ImGui.MenuItem($"{EditorIcons.Redo}  Redo", "Ctrl+Y")) { EditorUndo.Redo(); MarkSceneDirty(); }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("GameObject")) {
            DrawGameObjectMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window")) {
            ImGui.MenuItem("Entities", (string)null, ref showHierarchy);
            ImGui.MenuItem("Scene Components", (string)null, ref showSceneComponents);
            ImGui.MenuItem("Inspector", (string)null, ref showInspector);
            ImGui.MenuItem("Assets", (string)null, ref showBottom);
            ImGui.MenuItem("Console", (string)null, ref showConsole);
            ImGui.MenuItem("Statistics", (string)null, ref showStats);
            ImGui.MenuItem("Profiler", (string)null, ref profilerPanel.Open);
            ImGui.MenuItem("Build", (string)null, ref buildPanel.Open);
            ImGui.MenuItem("Tags & Layers", (string)null, ref tagsLayers.Open);
            ImGui.MenuItem("Settings", (string)null, ref settings.Open);
            ImGui.Separator();
            if (ImGui.MenuItem("Reset Layout")) {
                EditorLayout.DeleteSaved();
                resetLayoutRequested = true;
                showHierarchy = showSceneComponents = showInspector = showBottom = showConsole = true;
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help")) {
            ImGui.TextDisabled("Ballistic Engine Editor");
            ImGui.Separator();
            ImGui.TextDisabled("RMB + WASDQE : fly camera");
            ImGui.TextDisabled("W/E/R : move / rotate / scale");
            ImGui.TextDisabled("Hold V (move) : vertex snap");
            ImGui.TextDisabled("F : fly camera to selection");
            ImGui.TextDisabled("Ctrl+Shift+F : move selection to camera");
            ImGui.TextDisabled("Ctrl+C / Ctrl+V : copy / paste entity");
            ImGui.TextDisabled("Ctrl+D : duplicate    Ctrl+Z/Y : undo/redo");
            ImGui.TextDisabled("Double-click a view : fullscreen");
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
        return height;
    }

    void DrawGameObjectMenu() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.MenuItem("Create Empty")) {
            EditorUndo.Push("Create Empty");
            editorState.Select(scene.CreateEntity("Entity"));
        }

        ImGui.Separator();
        if (ImGui.BeginMenu($"{EditorIcons.Package}  3D Object")) {
            if (ImGui.MenuItem("Cube")) CreatePrimitive(PrimitiveKind.Cube);
            if (ImGui.MenuItem("Sphere")) CreatePrimitive(PrimitiveKind.Sphere);
            if (ImGui.MenuItem("Plane")) CreatePrimitive(PrimitiveKind.Plane);
            ImGui.EndMenu();
        }

        ImGui.Separator();
        if (ImGui.BeginMenu($"{EditorIcons.Lightbulb}  Light")) {
            if (ImGui.MenuItem("Directional Light")) CreateWithComponent<DirectionalLight>("Directional Light");
            if (ImGui.MenuItem("Point Light")) CreateWithComponent<PointLight>("Point Light");
            if (ImGui.MenuItem("Spot Light")) CreateWithComponent<SpotLight>("Spot Light");
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem($"{EditorIcons.Camera}  Camera")) CreateWithComponent<HDCamera>("Camera");
    }

    void CreateWithComponent<T>(string name) where T : Behaviour {
        EditorUndo.Push($"Create {name}");
        Entity entity = SceneManager.GetCurrentScene().CreateEntity(name);
        entity.AddComponent(typeof(T));
        PlaceInFrontOfCamera(entity);
        editorState.Select(entity);
    }

    void CreatePrimitive(PrimitiveKind kind) {
        EditorUndo.Push($"Create {kind}");
        Entity entity = Primitives.Create(SceneManager.GetCurrentScene(), kind);
        PlaceInFrontOfCamera(entity);
        editorState.Select(entity);
        MarkSceneDirty();
    }

    void PlaceInFrontOfCamera(Entity entity) {
        Transform cam = editorCamera.Transform;
        entity.transform.Position = cam.Position + cam.Forward * 6f;
    }

    // Runs `action` immediately if there are no unsaved changes, otherwise raises the prompt.
    void GuardUnsaved(Action action) {
        if (EditorUndo.IsDirty)
            pendingAfterSavePrompt = action;
        else
            action();
    }

    void DrawUnsavedPrompt() {
        if (pendingAfterSavePrompt is null)
            return;

        ImGui.OpenPopup("Unsaved Changes");
        ImGuiViewportPtr vp = ImGui.GetMainViewport();
        SysVec2 center = vp.Pos + vp.Size * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new SysVec2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal("Unsaved Changes", ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.PushStyleColor(ImGuiCol.Text, new SysVec4(0.95f, 0.80f, 0.30f, 1f));
            ImGui.Text(EditorIcons.Warning);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 8 * S);
            ImGui.Text("The scene has unsaved changes.");
            ImGui.Spacing();
            ImGui.Spacing();
            SysVec4 accent = EditorPrefs.Current.Accent;
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(accent.X, accent.Y, accent.Z, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(accent.X, accent.Y, accent.Z, 0.75f));
            if (ImGui.Button($"{EditorIcons.Save}  Save", new SysVec2(110 * S, 0))) {
                SaveScene();
                RunPending();
            }
            ImGui.PopStyleColor(2);
            ImGui.SameLine();
            if (ImGui.Button("Discard", new SysVec2(110 * S, 0)))
                RunPending();
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new SysVec2(110 * S, 0))) {
                pendingAfterSavePrompt = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        void RunPending() {
            Action action = pendingAfterSavePrompt;
            pendingAfterSavePrompt = null;
            ImGui.CloseCurrentPopup();
            action?.Invoke();
            MarkSceneDirty();
        }
    }

    // Assets and Console share the bottom strip as tabs (Unity-style).
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

        // Project + scene name; a small accent dot marks unsaved changes.
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(bootstrap.Project.Manifest.Name);
        ImGui.SameLine(0, 8 * S);
        ImGui.TextDisabled("/");
        ImGui.SameLine(0, 8 * S);
        ImGui.Text(scene.Name);
        if (EditorUndo.IsDirty) {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unsaved changes (Ctrl+S to save)");
            SysVec2 dot = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddCircleFilled(
                new SysVec2(dot.X + 7 * S, (ImGui.GetItemRectMin().Y + dot.Y) * 0.5f),
                3.5f * S, ImGui.GetColorU32(ImGuiCol.CheckMark));
            ImGui.SameLine(0, 14 * S);
            ImGui.Dummy(new SysVec2(1, 0));
        }

        ImGui.SameLine(0, 24 * S);
        GizmoModeToolbar();

        ImGui.SameLine(0, 24 * S);
        UndoRedoToolbar();

        // Center: Play/Stop + Pause + Step transport (Unity-style). Pause/Step only matter in play.
        float buttonW = 46 * S;
        float gap = 6 * S;
        float groupW = buttonW * 3 + gap * 2;
        ImGui.SameLine((ImGui.GetWindowWidth() - groupW) * 0.5f);

        if (SceneManager.IsPlaying) {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.66f, 0.26f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.78f, 0.33f, 0.24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(0.85f, 0.38f, 0.27f, 1f));
            if (ImGui.Button(EditorIcons.Stop, new SysVec2(buttonW, 0))) {
                SceneManager.StopPlay();
                Cursor.Mode = CursorMode.Normal; // clear any leftover lock intent from the play session
                editorState.Selected = null;
                pendingFocusWindow = EditorLayout.SceneView;
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stop (exit play mode)");
        }
        else {
            // Unity's compile-error lock: while the latest script compile failed, the Play
            // button is disabled with the reason in its tooltip (StartPlay also self-guards).
            var playBlockedReason = SceneManager.PlayBlocked?.Invoke();

            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.16f, 0.42f, 0.24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.20f, 0.53f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(0.24f, 0.62f, 0.35f, 1f));
            ImGui.BeginDisabled(playBlockedReason is not null);
            var playClicked = ImGui.Button(EditorIcons.Play, new SysVec2(buttonW, 0));
            ImGui.EndDisabled();
            if (playBlockedReason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Play blocked: {playBlockedReason}");
            if (playClicked) {
                // Persist edits to disk before play (Unity-style): play mode only keeps an in-memory
                // snapshot that Stop restores, so a close/crash mid-play would otherwise lose unsaved
                // edits (collider sizes, etc.). Only when there's something to save and a file to save to.
                if (EditorUndo.IsDirty && !string.IsNullOrEmpty(SceneCommands.CurrentScenePath))
                    SceneCommands.Save();
                SceneManager.StartPlay();
                pendingFocusWindow = EditorLayout.GameView;
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Play");
        }

        // Pause toggle: lit while paused. Only meaningful in play mode.
        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(!SceneManager.IsPlaying);
        bool paused = SceneManager.IsPaused;
        if (paused) {
            ImGui.PushStyleColor(ImGuiCol.Button, EditorPrefs.Current.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EditorPrefs.Current.Accent);
        }
        if (ImGui.Button(EditorIcons.Pause, new SysVec2(buttonW, 0)))
            SceneManager.IsPaused = !SceneManager.IsPaused;
        if (paused)
            ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(paused ? "Resume" : "Pause");

        // Step one frame: advances a single frame while paused.
        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(!(SceneManager.IsPlaying && SceneManager.IsPaused));
        if (ImGui.Button(EditorIcons.ChevronRight, new SysVec2(buttonW, 0))) {
            SceneManager.StepFrame();
            MarkSceneDirty();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Step one frame (while paused)");
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        // Right side: live-refresh toggle, save. (Stats toggle lives on the Scene/Game view bar.)
        float rightBlock = 95 * S;
        ImGui.SameLine(ImGui.GetWindowWidth() - rightBlock);
        ToggleIconButton("alwaysrefresh", EditorIcons.Refresh, ref alwaysRefresh,
            "Always refresh the viewport (off = re-render only on change)");
        ImGui.SameLine(0, 2);
        if (EditorIcons.GhostButton("save", EditorIcons.Save, "Save scene (Ctrl+S)"))
            SaveScene();
    }

    // FPS readout doubling as the frame-rate limiter: click to pick a preset (same options as
    // Settings > Viewport > Frame rate limit; both edit the same preference). Lives in the
    // Scene/Game resolution bar; the label is precomputed by the caller for right-alignment.
    void DrawFpsButton(string label) {
        int limit = EditorPrefs.Current.FrameRateLimit;
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        var clicked = EditorIcons.GhostButton("fpslimit", label,
            limit <= 0 ? "Frame rate limit: VSync (click to change)"
                       : $"Frame rate limit: {limit} fps (click to change)");
        ImGui.PopStyleColor();
        if (clicked)
            ImGui.OpenPopup("##fpslimit");

        if (!ImGui.BeginPopup("##fpslimit"))
            return;

        ImGui.TextDisabled("Frame rate limit");
        ImGui.Separator();
        for (var i = 0; i < SettingsPanel.FrameLimitOptions.Length; i++) {
            var selected = limit == SettingsPanel.FrameLimitOptions[i];
            if (ImGui.MenuItem(SettingsPanel.FrameLimitLabels[i], (string)null, selected)) {
                EditorPrefs.Current.FrameRateLimit = SettingsPanel.FrameLimitOptions[i];
                ApplyFrameRateLimit();
                EditorPrefs.Save();
            }
        }
        // Custom limit (item 13): type any value; 0 = unlimited. Enter applies.
        ImGui.Separator();
        ImGui.TextDisabled("Custom (0 = unlimited):");
        ImGui.SetNextItemWidth(120);
        int custom = limit;
        if (ImGui.InputInt("##customfps", ref custom, 5, 30, ImGuiInputTextFlags.EnterReturnsTrue)) {
            EditorPrefs.Current.FrameRateLimit = Math.Max(0, custom);
            ApplyFrameRateLimit();
            EditorPrefs.Save();
        }
        ImGui.EndPopup();
    }

    // Icon button that stays accent-lit while the bound flag is on.
    static void ToggleIconButton(string id, string icon, ref bool value, string tooltip) {
        SysVec4 accent = ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark];
        // Capture the pushed state BEFORE the button — clicking flips `value`, so guarding the Pop on
        // the (possibly flipped) value would unbalance the color stack (the assertion you hit).
        bool pushed = value;
        if (pushed)
            ImGui.PushStyleColor(ImGuiCol.Text, accent);
        if (EditorIcons.GhostButton(id, icon, tooltip))
            value = !value;
        if (pushed)
            ImGui.PopStyleColor();
    }

    // Move/Rotate/Scale as a segmented control: one dark backing pill, accent on the active mode.
    void GizmoModeToolbar() {
        float h = ImGui.GetFrameHeight();
        float bw = 62 * S;
        SysVec2 start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(
            start - new SysVec2(3 * S, 3 * S),
            start + new SysVec2(bw * 3 + 4 + 3 * S, h + 3 * S),
            ImGui.GetColorU32(new SysVec4(0, 0, 0, 0.30f)), 6f);

        GizmoModeButton("Move", GizmoMode.Translate, bw, "Move (W)");
        ImGui.SameLine(0, 2);
        GizmoModeButton("Rotate", GizmoMode.Rotate, bw, "Rotate (E)");
        ImGui.SameLine(0, 2);
        GizmoModeButton("Scale", GizmoMode.Scale, bw, "Scale (R)");
    }

    // ---- Viewport (Scene / Game tabs) ----------------------------------------

    // A thin bar at the top of a view: resolution preset + render-scale slider (Unity's "Scale"),
    // and the resulting pixel size. The render target uses these; the image displays fit-to-panel.
    void ResolutionBar(ViewportResolution res, string id) {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Res");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * S);
        ImGui.Combo($"##res{id}", ref res.PresetIndex,
            ViewportResolution.PresetLabels, ViewportResolution.PresetLabels.Length);

        ImGui.SameLine(0, 16 * S);
        ImGui.TextDisabled($"{EditorIcons.Search}");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(140 * S);
        ImGui.SliderFloat($"##zoom{id}", ref res.Zoom, 1f, 8f, "%.1fx");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Magnify the rendered image to inspect it up close (like zooming into a photo).");
        ImGui.SameLine();
        if (ImGui.SmallButton($"1x##zoomreset{id}"))
            res.Zoom = 1f;

        // Right side, laid out right-to-left: render resolution, FPS-limit button, and (Scene
        // view only) the grid / gizmos / space / snap controls as compact icon toggles.
        float pad2 = ImGui.GetStyle().FramePadding.X * 2;
        float right = ImGui.GetWindowWidth() - 14 * S;
        void RightAlign(float w) { right -= w; ImGui.SameLine(right); right -= 6 * S; }

        SysVec2 rs = id == "scene" ? sceneViewSize : gameViewSize;
        var resText = $"{(int)rs.X} x {(int)rs.Y}";
        RightAlign(ImGui.CalcTextSize(resText).X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(resText);

        var fpsLabel = $"{runtime.Window.FrameRate:0} fps {EditorIcons.ChevronDown}";
        RightAlign(ImGui.CalcTextSize(fpsLabel).X + pad2);
        DrawFpsButton(fpsLabel);

        // Stats overlay toggle, Unity's Game-view "Stats" button style (the overlay's X also closes it).
        var statsLabel = $"{EditorIcons.Info} Stats";
        RightAlign(ImGui.CalcTextSize(statsLabel).X + pad2);
        ToggleIconButton($"statsbar{id}", statsLabel, ref showStats, "Statistics overlay");

        // (The maximize BUTTON was removed — double-click any panel's tab to fullscreen it, Esc to
        // restore. Works for every panel now, so a dedicated viewport button is redundant.)

        if (id == "scene") {
            EditorPrefs prefs = EditorPrefs.Current;

            // Shading-mode dropdown: Shaded / Wireframe / Normals / Depth (renderer debug views,
            // Scene view only). Applies to the editor camera so you can inspect geometry/normals/
            // depth without lighting. Sets Renderer.DebugViewMode and forces a repaint.
            var modeNames = new[] { "Shaded", "Wireframe", "Normals", "Depth" };
            var curMode = (int)Renderer.DebugViewMode;
            var modeLabel = $"{modeNames[curMode]} {EditorIcons.ChevronDown}";
            RightAlign(ImGui.CalcTextSize(modeLabel).X + pad2);
            if (EditorIcons.GhostButton("shadingmode", modeLabel, "Shading / debug view mode"))
                ImGui.OpenPopup("##shadingmode");
            if (ImGui.BeginPopup("##shadingmode")) {
                ImGui.TextDisabled("Shading Mode");
                ImGui.Separator();
                for (var i = 0; i < modeNames.Length; i++) {
                    if (ImGui.MenuItem(modeNames[i], (string)null, curMode == i)) {
                        Renderer.DebugViewMode = (HDRenderer.DebugView)i;
                        editorState.MarkViewportDirty();
                    }
                }
                ImGui.EndPopup();
            }

            // Snap indicator chip.
            var snapOn = ImGui.GetIO().KeyCtrl;
            RightAlign(ImGui.CalcTextSize("Snap").X);
            ImGui.AlignTextToFramePadding();
            if (snapOn)
                ImGui.TextColored(ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark], "Snap");
            else
                ImGui.TextDisabled("Snap");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Ctrl while dragging a gizmo to snap.");

            // Gizmo space toggle: globe = world axes, cube = the object's local axes.
            var world = gizmo.Space == GizmoSpace.World;
            var spaceIcon = world ? EditorIcons.World : EditorIcons.Package;
            RightAlign(ImGui.CalcTextSize(spaceIcon).X + pad2);
            if (EditorIcons.GhostButton("gizmospace", spaceIcon,
                    world ? "Gizmo space: World (click for Local)" : "Gizmo space: Local (click for World)"))
                gizmo.Space = world ? GizmoSpace.Local : GizmoSpace.World;

            // Component gizmos toggle.
            RightAlign(ImGui.CalcTextSize(EditorIcons.Pin).X + pad2);
            var gizmosBefore = showGizmos;
            ToggleIconButton("gizmostoggle", EditorIcons.Pin, ref showGizmos, "Component gizmos");
            if (gizmosBefore != showGizmos) { prefs.ShowGizmos = showGizmos; EditorPrefs.Save(); }

            // Grid toggle.
            RightAlign(ImGui.CalcTextSize(EditorIcons.Grid).X + pad2);
            var grid = prefs.ShowGrid;
            ToggleIconButton("gridtoggle", EditorIcons.Grid, ref grid, "Viewport grid");
            if (grid != prefs.ShowGrid) { prefs.ShowGrid = grid; EditorPrefs.Save(); }
        }

        ImGui.Separator();
    }

    // Double-clicking a viewport window's tab/title strip toggles fullscreen for that view. Call right
    // after Begin. For a DOCKED window the tab bar sits ABOVE the content origin (GetWindowPos().Y), so
    // the hit band extends upward by ~2 frame heights to cover the dock tab; for a floating window it
    // covers the title bar. The horizontal span is the window width. Excludes the content area so a
    // double-click on the 3D image (gizmo/selection) never maximizes.
    void MaximizeOnTitleDoubleClick() => MaximizePanelOnTitleDoubleClick(
        gameViewFocused ? EditorLayout.GameView : EditorLayout.SceneView);

    // Double-clicking a window's tab/title strip toggles fullscreen for THAT panel (works for every
    // dockable panel now, not just the viewports). Call right after the panel's Begin. For a DOCKED
    // window the tab bar sits ABOVE the content origin, so the hit band extends upward by ~1.4 frame
    // heights; for a floating window it covers the title bar. Excludes the content area.
    void MaximizePanelOnTitleDoubleClick(string panelName) {
        if (!ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            return;
        SysVec2 mn = ImGui.GetWindowPos();
        float w = ImGui.GetWindowSize().X;
        float tabH = ImGui.GetFrameHeight() * 1.4f;
        SysVec2 mouse = ImGui.GetIO().MousePos;
        bool overTab = mouse.X >= mn.X && mouse.X <= mn.X + w &&
                       mouse.Y >= mn.Y - tabH && mouse.Y <= mn.Y + 2f;
        if (overTab)
            maximizedPanel = maximizedPanel == panelName ? null : panelName;
    }

    // Draws one panel filling the whole work area while maximized (anything except the viewports,
    // which take DrawMaximizedViewport). Routes by the panel's layout name to its contents.
    void DrawMaximizedPanel(string name, SysVec2 pos, SysVec2 size) {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin(name, flags)) {
            MaximizePanelOnTitleDoubleClick(name); // double-click its title again to restore
            if (name == EditorLayout.Entities) hierarchy.DrawEntitiesContents();
            else if (name == EditorLayout.SceneComponents) hierarchy.DrawSceneContents();
            else if (name == EditorLayout.Inspector) inspector.DrawContents();
            else if (name == EditorLayout.Assets) assets.DrawContents();
            else if (name == EditorLayout.Console) console.DrawContents();
        }
        ImGui.End();
    }

    // Scene and Game are now SEPARATE dockable windows (default-tabbed together in the center dock
    // node). Each is its own ImGui window so it can be split out / viewed side by side. Reset the
    // Game-view focus/hover ONCE before either window runs: only the Game window sets them true, and
    // stale gameViewFocused would keep game input (cursor lock) live while editing in the Scene view.
    void DrawViewportWindows() {
        gameViewFocused = false;
        gameViewHovered = false;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(1, 1));

        // Play/stop (and scene-open) request focusing one view so it surfaces above its dock tab group.
        if (pendingFocusWindow == EditorLayout.SceneView) ImGui.SetNextWindowFocus();
        if (ImGui.Begin(EditorLayout.SceneView)) {
            MaximizeOnTitleDoubleClick();   // double-click the Scene dock tab to (un)fullscreen
            // The view whose window is focused drives offscreen render selection (OnRender).
            if (ImGui.IsWindowFocused()) sceneTabActive = true;
            SceneTabContents();
        }
        ImGui.End();

        if (pendingFocusWindow == EditorLayout.GameView) ImGui.SetNextWindowFocus();
        if (ImGui.Begin(EditorLayout.GameView)) {
            MaximizeOnTitleDoubleClick();   // double-click the Game dock tab to (un)fullscreen
            if (ImGui.IsWindowFocused()) sceneTabActive = false;
            GameTabContents();
        }
        ImGui.End();

        pendingFocusWindow = null;
        ImGui.PopStyleVar();
    }

    // Maximized fullscreen: one fixed window showing whichever view was last active (Scene or Game).
    void DrawMaximizedViewport(SysVec2 pos, SysVec2 size) {
        gameViewFocused = false;
        gameViewHovered = false;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(1, 1));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (ImGui.Begin("##viewportmax", PanelFlags | ImGuiWindowFlags.NoTitleBar)) {
            if (sceneTabActive)
                SceneTabContents();
            else
                GameTabContents();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    void SceneTabContents() {
        ResolutionBar(sceneRes, "scene");

        SysVec2 avail = ImGui.GetContentRegionAvail();
        if (avail.X > 0 && avail.Y > 0) scenePanelSize = avail;
        sceneViewSize = sceneRes.RenderSize(scenePanelSize);

        (SysVec2 dispSize, SysVec2 dispOffset) = sceneRes.DisplayRect(scenePanelSize);
        if (dispOffset.X > 0 || dispOffset.Y > 0)
            ImGui.SetCursorPos(ImGui.GetCursorPos() + dispOffset);

        (SysVec2 uv0, SysVec2 uv1) = sceneRes.ZoomUVs();
        ImGui.Image(Tex(Renderer.SceneColorTextureId), dispSize, uv0, uv1);
        SysVec2 imageMin = ImGui.GetItemRectMin();
        SysVec2 imageSize = dispSize;
        // Hairline frame so the rendered image reads as a deliberate surface, not a raw blit.
        ImGui.GetWindowDrawList().AddRect(imageMin, imageMin + imageSize,
            ImGui.GetColorU32(new SysVec4(1, 1, 1, 0.06f)));
        sceneViewHovered = ImGui.IsItemHovered();
        gameViewFocused = false;
        gameViewHovered = false;

        // Scene-view shortcuts (not while flying â€” the camera uses WASD too).
        if (sceneViewHovered && !editorInput.RightMouseDown && !ImGui.GetIO().KeyCtrl) {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) gizmo.Mode = GizmoMode.Translate;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) gizmo.Mode = GizmoMode.Rotate;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) gizmo.Mode = GizmoMode.Scale;
        }

        // Focus/clipboard keys work from ANY panel while the Scene tab is showing (Unity-style), so
        // selecting in the Hierarchy and pressing F flies the camera there without needing to hover the
        // viewport first. Suppressed while typing or flying. Mapping matches Unity exactly:
        //   F             -> fly the camera to frame the selection,
        //   Ctrl+Shift+F  -> move the selection to the camera (Align With View),
        //   Ctrl+C/V      -> copy / paste the selected entity.
        // Modifiers AND the key edges are read from RAW OpenTK (editorInput, the same source the global
        // Ctrl+Z/S use), not ImGui's io â€” so Ctrl/Shift can never read a frame stale relative to the F
        // edge, which was making Ctrl+Shift+F fall through to the plain-F (frame) path.
        if (!editorInput.RightMouseDown && !imgui.WantTextInput) {
            bool ctrl = editorInput.CtrlDown;
            bool shift = editorInput.ShiftDown;

            if (editorInput.KeyPressed(Keys.F)) {
                if (ctrl && shift)
                    AlignSelectedToView();
                else if (!ctrl && !shift)
                    FocusSelected();
            }

            if (ctrl && !shift && editorInput.KeyPressed(Keys.C))
                CopySelected();
            if (ctrl && !shift && editorInput.KeyPressed(Keys.V))
                PasteClipboard();
        }

        // Gizmo/grid project into the on-screen image rect. When the view is magnified (zoom > 1),
        // the image shows a centered 1/zoom crop, so the projection rect is enlarged by zoom around
        // the same center â€” projected points then land correctly on the magnified picture (ImGui
        // clips the overlay to the panel automatically).
        float zoom = Math.Max(1f, sceneRes.Zoom);
        SysVec2 center = imageMin + imageSize * 0.5f;
        SysVec2 gizmoSize = imageSize * zoom;
        SysVec2 gizmoMin = center - gizmoSize * 0.5f;

        if (EditorPrefs.Current.ShowGrid)
            ViewportGrid.Draw(editorCamera, gizmoMin, gizmoSize, EditorPrefs.Current.GridSize);

        if (showGizmos)
            DrawComponentGizmos(gizmoMin, gizmoSize);

        // Arm vertex snapping while V is held over the viewport (raw key so it works regardless of which
        // panel has ImGui focus, suppressed while typing). On-demand rendering means we must keep
        // repainting while it's armed so the snap marker tracks the moving cursor.
        VertexSnap.Held = sceneViewHovered && !imgui.WantTextInput && editorInput.KeyDown(Keys.V);
        if (VertexSnap.Held)
            MarkSceneDirty();

        // The terrain brush ring follows the cursor, so keep the viewport repainting while it's armed
        // and the mouse is over the Scene view (same idea as VertexSnap.Held above).
        if (TerrainTool.Armed && sceneViewHovered)
            MarkSceneDirty();

        if (editorState.Selected is not null)
            gizmo.Draw(editorCamera, editorState.Selected, gizmoMin, gizmoSize,
                sceneViewHovered && !ColliderHandles.IsInteracting && !TerrainTool.Armed);

        // Click-to-select (Unity-style): a clean left-click in the viewport (no drag) picks the mesh
        // under the cursor. Runs AFTER the gizmo draw so gizmo/collider hover this frame can veto the
        // click â€” a press on a handle moves it instead of picking through it.
        HandleScenePick(gizmoMin, gizmoSize);

        DrawOrientationGizmo(imageMin, imageSize);   // orientation cube stays panel-anchored, not zoomed

        // Stats pinned to the view's top-right, below the orientation cube.
        if (showStats && !stats.Draw(runtime.Window.FrameRate, editorCpuMs, sceneViewSize, S,
                imageMin, imageSize, 105 * S, RenderStats.Scene))
            showStats = false;
    }

    // Orientation cube/triad (Phase 7) â€” drawn at the Scene view's top-right; click an axis to snap.
    void DrawOrientationGizmo(SysVec2 imageMin, SysVec2 imageSize) {
        OrientationGizmo.Draw(editorCamera, imageMin, imageSize, S, sceneViewHovered);
    }

    // Runs every active component's OnDrawGizmos, plus OnDrawGizmosSelected for the selected entity
    // (drawn even when the component is disabled), mirroring Unity. Painted with the window draw list
    // so it clips to the Scene image.
    void DrawComponentGizmos(SysVec2 imageMin, SysVec2 imageSize) {
        gizmoDrawer.Begin(editorCamera, imageMin, imageSize, ImGui.GetWindowDrawList());

        // Drain the runtime debug-draw buffer (Debug.DrawLine/DrawRay from game scripts) through the
        // same camera projection the gizmos use. Drawn first so component handles paint on top.
        // Expiry (single-frame + timed cleanup) runs once per engine frame in UpdateFrame.
        foreach (DebugDraw.Segment segment in DebugDraw.Segments) {
            gizmoDrawer.Color = segment.Color;
            gizmoDrawer.DrawLine(segment.From, segment.To);
        }
        gizmoDrawer.Color = Vector3.One;

        // User-overridable callbacks run guarded (ScriptGuard): a throwing gizmo in a game script
        // must not take the Scene view down â€” repeat offenders get auto-disabled.
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
            if (!entity.IsActive)
                continue;
            foreach (Behaviour behaviour in entity.Behaviours) {
                if (!behaviour.IsEnabled)
                    continue;
                try { behaviour.OnDrawGizmos(gizmoDrawer); }
                catch (Exception e) { ScriptGuard.ReportRepeating(behaviour, "OnDrawGizmos", e); }
            }
        }

        // Scene-wide components (irradiance volume bounds, ...) get the same treatment,
        // with the Scene-tab selection driving OnDrawGizmosSelected.
        foreach (SceneBehaviour sceneBehaviour in SceneManager.GetCurrentScene().SceneBehaviours) {
            if (!sceneBehaviour.IsActive)
                continue;
            try { sceneBehaviour.OnDrawGizmos(gizmoDrawer); }
            catch (Exception e) { ScriptGuard.ReportRepeating(sceneBehaviour, "OnDrawGizmos", e); }
        }

        if (editorState.Selected is { IsActive: true } selected) {
            foreach (Behaviour behaviour in selected.Behaviours) {
                try { behaviour.OnDrawGizmosSelected(gizmoDrawer); }
                catch (Exception e) { ScriptGuard.ReportRepeating(behaviour, "OnDrawGizmosSelected", e); }
            }

            // Colliders get Unity-style drag handles for resizing in-view. Hover is suppressed
            // while the transform gizmo drags so one click can't grab both.
            foreach (Behaviour behaviour in selected.Behaviours)
                if (behaviour is Collider collider &&
                    ColliderHandles.Draw(collider, editorCamera, imageMin, imageSize,
                        ImGui.GetWindowDrawList(), sceneViewHovered && !gizmo.IsInteracting))
                    MarkSceneDirty();

            // Terrain gets a Scene-view sculpt brush, active only while the Inspector arms it. Hover is
            // suppressed while the transform gizmo/collider handles interact so a click can't grab both.
            // Disarm if the selection has no terrain, so a stale Armed flag can't block click-to-select.
            Terrain selectedTerrain = selected.GetComponent<Terrain>();
            if (selectedTerrain is null)
                TerrainTool.Armed = false;
            else if (TerrainTool.Draw(selectedTerrain, editorCamera, imageMin, imageSize,
                         ImGui.GetWindowDrawList(),
                         sceneViewHovered && !gizmo.IsInteracting && !ColliderHandles.IsInteracting))
                MarkSceneDirty();
        }
        else {
            TerrainTool.Armed = false; // nothing selected — never leave the brush armed
        }

        if (editorState.SelectedSceneBehaviour is { } selectedSceneBehaviour) {
            try { selectedSceneBehaviour.OnDrawGizmosSelected(gizmoDrawer); }
            catch (Exception e) { ScriptGuard.ReportRepeating(selectedSceneBehaviour, "OnDrawGizmosSelected", e); }

            // Irradiance volumes get draggable face handles for resizing the box in-view.
            if (selectedSceneBehaviour is IrradianceVolume irradianceVolume &&
                VolumeBoundsHandles.Draw(irradianceVolume, editorCamera, imageMin, imageSize,
                    ImGui.GetWindowDrawList(), sceneViewHovered))
                MarkSceneDirty();
        }
    }

    // Scene-view click-to-select. A press that lands on empty viewport (not a gizmo handle, not the
    // start of a fly/drag) becomes a select-candidate; on release, if the cursor barely moved, raycast
    // the scene and select the mesh under it (or clear selection on a miss, Unity-style). The press/
    // release split means a click-drag that flies the camera or grabs the gizmo never also selects.
    void HandleScenePick(SysVec2 viewMin, SysVec2 viewSize) {
        // Conditions under which a left-press can START a pick: over the viewport, not flying, and no
        // gizmo/handle interaction is claiming the click this frame.
        bool gizmoBusy = gizmo.IsInteracting || gizmo.IsHovered ||
                         ColliderHandles.IsInteracting || VertexSnap.Held ||
                         TerrainTool.Armed || TerrainTool.IsInteracting;
        bool canStart = sceneViewHovered && !editorInput.RightMouseDown && !gizmoBusy &&
                        !imgui.WantTextInput;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            pickPressValid = canStart;
            pickPressPos = ImGui.GetMousePos();
        }

        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left) || !pickPressValid)
            return;
        pickPressValid = false;

        // Reject if it turned into a drag (camera nudge, marquee, accidental motion).
        const float clickSlop = 4f;
        if (SysVec2.Distance(ImGui.GetMousePos(), pickPressPos) > clickSlop)
            return;

        Matrix4 vp = editorCamera.GetViewMatrix() * editorCamera.GetProjectionMatrix();
        Entity hit = ScenePicker.Pick(vp, viewMin, viewSize, ImGui.GetMousePos());

        // Select the hit, or clear selection when clicking empty space (Unity behaviour). Only act when
        // it actually changes selection so an idle click doesn't thrash the inspector.
        if (!ReferenceEquals(hit, editorState.Selected)) {
            if (hit is not null)
                editorState.Select(hit);
            else
                editorState.Selected = null;
            MarkSceneDirty();
        }
    }

    // F: fly the Scene camera to the selected entity, framing its actual geometry (Unity-style).
    // The target/radius come from the world-space bounds of the entity's (and its children's) mesh
    // renderers â€” so a big building frames out far and a small prop frames in close, instead of the
    // old scale-based guess that barely moved for unscaled meshes.
    void FocusSelected() {
        Entity selected = editorState.Selected;
        if (selected is null)
            return;

        if (EditorBounds.TryGetWorldBounds(selected, out Vector3 center, out float radius))
            editorCamera.Focus(center, radius);
        else
            // No renderable geometry (empty/light/camera): frame the pivot at a sensible distance.
            editorCamera.Focus(selected.transform.WorldPosition, 1f);
    }

    // Ctrl+Shift+F (Unity's "Align With View"): the selected entity copies the scene camera's world
    // position and rotation exactly (e.g. to place a game camera or light right where the view is).
    void AlignSelectedToView() {
        Entity selected = editorState.Selected;
        if (selected is null)
            return;

        EditorUndo.Push("Align To View");
        Transform cam = editorCamera.Transform;
        selected.transform.Position = cam.Position;
        selected.transform.Rotation = cam.Rotation;
        MarkSceneDirty();
    }

    // Ctrl+C: remember the selected entity for a later paste (clone-on-paste, Unity-style).
    void CopySelected() {
        if (editorState.Selected is { } selected)
            EditorClipboard.Copy(selected);
    }

    // Ctrl+V: clone the copied entity into the current scene, select it, and repaint. Undo is pushed
    // only when there's actually something to paste, so a stray Ctrl+V doesn't litter the undo stack.
    void PasteClipboard() {
        if (!EditorClipboard.HasCopy)
            return;

        EditorUndo.Push("Paste");
        if (EditorClipboard.Paste(SceneManager.GetCurrentScene()) is { } copy) {
            editorState.Select(copy);
            MarkSceneDirty();
        }
    }

    unsafe void GameTabContents() {
        ResolutionBar(gameRes, "game");

        SysVec2 avail = ImGui.GetContentRegionAvail();
        if (avail.X > 0 && avail.Y > 0) gamePanelSize = avail;
        gameViewSize = gameRes.RenderSize(gamePanelSize);

        sceneViewHovered = false;

        if (FindSceneCamera() is not null) {
            (SysVec2 dispSize, SysVec2 dispOffset) = gameRes.DisplayRect(gamePanelSize);
            if (dispOffset.X > 0 || dispOffset.Y > 0)
                ImGui.SetCursorPos(ImGui.GetCursorPos() + dispOffset);

            (SysVec2 uv0, SysVec2 uv1) = gameRes.ZoomUVs();
            ImGui.Image(Tex(Renderer.GameColorTextureId), dispSize, uv0, uv1);
            gameViewFocused = ImGui.IsWindowFocused();
            gameViewHovered = ImGui.IsItemHovered(); // is the MOUSE over the game image specifically

            // Route pointer input to active game UIs using the game IMAGE's on-screen rect (position +
            // displayed size), so hit-testing maps window-space mouse coords into the UI's logical space
            // correctly — independent of the panel's offset and display scale. Gated like engine input
            // (Input.Enabled = play mode + game focused), so editing never leaks clicks into the UI.
            if (Input.Enabled) {
                SysVec2 imgMin = ImGui.GetItemRectMin();
                var panelRect = new BallisticEngine.UI.Rect(imgMin.X, imgMin.Y, dispSize.X, dispSize.Y);
                foreach (var doc in BallisticEngine.UI.UIDocument.Active)
                    doc.ProcessInput(panelRect);
            }

            // Stats pinned to the view's top-right (no orientation cube here, so right at the top).
            if (showStats && !stats.Draw(runtime.Window.FrameRate, editorCpuMs, gameViewSize, S,
                    ImGui.GetItemRectMin(), dispSize, 10 * S, RenderStats.Game))
                showStats = false;
        }
        else {
            ImGui.Dummy(new SysVec2(0, avail.Y * 0.38f));
            if (ImGuiController.HasIcons) {
                float iconSize = 44 * S;
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - iconSize) * 0.5f);
                ImGui.GetWindowDrawList().AddText(ImGuiController.LargeIcons, iconSize,
                    ImGui.GetCursorScreenPos(), ImGui.GetColorU32(new SysVec4(1, 1, 1, 0.08f)),
                    EditorIcons.Camera);
                ImGui.Dummy(new SysVec2(iconSize, iconSize));
                ImGui.Spacing();
            }
            CenteredText("No camera in the scene");
            CenteredText("Add an HDCamera component to see the game view.");
            gameViewFocused = false;
            gameViewHovered = false;
        }
    }

    static void CenteredText(string text) {
        float w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) * 0.5f);
        ImGui.TextDisabled(text);
    }

    // Copies OS-dropped files into the browser's current folder and runs the import pipeline â€”
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
            AsyncAssetImport.Request(
                copied == 1 ? $"Importing {Path.GetFileName(files[0])}..." : $"Importing {copied} files...",
                onFinished: () => assets.InvalidateThumbnails());
    }


    // Undo / Redo buttons with the action name in the tooltip, plus a history dropdown that jumps
    // back multiple steps at once (Unity-style).
    void UndoRedoToolbar() {
        ImGui.BeginDisabled(!EditorUndo.CanUndo);
        if (EditorIcons.GhostButton("undo", EditorIcons.Undo, null, 34 * S)) { EditorUndo.Undo(); MarkSceneDirty(); }
        ImGui.EndDisabled();
        if (EditorUndo.CanUndo && ImGui.IsItemHovered())
            ImGui.SetTooltip($"Undo {EditorUndo.UndoLabel} (Ctrl+Z)");

        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(!EditorUndo.CanRedo);
        if (EditorIcons.GhostButton("redo", EditorIcons.Redo, null, 34 * S)) { EditorUndo.Redo(); MarkSceneDirty(); }
        ImGui.EndDisabled();
        if (EditorUndo.CanRedo && ImGui.IsItemHovered())
            ImGui.SetTooltip($"Redo {EditorUndo.RedoLabel} (Ctrl+Y)");

        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(!EditorUndo.CanUndo);
        if (EditorIcons.GhostButton("undohist", EditorIcons.ChevronDown, null, 22 * S))
            ImGui.OpenPopup("##undohistory");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Undo history");

        if (ImGui.BeginPopup("##undohistory")) {
            ImGui.TextDisabled("Undo history");
            ImGui.Separator();
            var index = 0;
            foreach (var label in EditorUndo.History()) {
                if (ImGui.Selectable($"{label}##h{index}")) {
                    EditorUndo.UndoTo(index);
                    MarkSceneDirty();
                }
                index++;
            }
            if (index == 0)
                ImGui.TextDisabled("(empty)");
            ImGui.EndPopup();
        }
    }

    void GizmoModeButton(string label, GizmoMode mode, float width, string tooltip) {
        var active = gizmo.Mode == mode;
        if (active) {
            SysVec4 accent = EditorPrefs.Current.Accent;
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(accent.X, accent.Y, accent.Z, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(accent.X, accent.Y, accent.Z, 0.70f));
        }
        else {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(1, 1, 1, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }
        if (ImGui.Button(label, new SysVec2(width, 0)))
            gizmo.Mode = mode;
        ImGui.PopStyleColor(active ? 2 : 3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    // Runs on the render thread once the startup asset import completes. Routes the startup scene
    // through SceneCommands.Open so it gets the same background prefetch (meshes/textures decoded on
    // workers) â€” opening the startup scene no longer blocks the render thread either.
    void LoadStartupScene() {
        // Reopen the scene you were last editing in THIS project (persisted in EditorPrefs), so closing
        // and relaunching the editor lands you back where you left off. Fall back to the project's
        // StartupScene when there's no remembered scene or its file is gone (deleted/renamed/moved).
        var project = bootstrap.Project;
        var lastScene = EditorPrefs.GetLastScene(project.RootPath);
        if (!string.IsNullOrEmpty(lastScene) && File.Exists(project.ResolveAbsolute(lastScene))) {
            SceneCommands.Open(lastScene);
            return;
        }

        var startup = project.Manifest.StartupScene;
        if (!string.IsNullOrEmpty(startup))
            SceneCommands.Open(startup);
    }

    void SaveScene() => SceneCommands.Save();

    // Recompiles Assets\**\*.cs and hot-swaps the script assembly (Ctrl+R / File menu). Runs
    // synchronously on the render thread â€” same tradeoff as the rest of the asset pipeline; the
    // build is a couple of seconds. On compile errors nothing changes (errors land in the
    // Console); on success the scene is rebuilt from a YAML snapshot over the new types, so the
    // stale selection clears itself via ClearIfDestroyed next frame.
    void RebuildScripts() {
        if (AsyncAssetImport.IsBusy || SceneCommands.IsLoading)
            return;

        if (bootstrap.ReloadGameScripts())
            MarkSceneDirty();
    }
}
