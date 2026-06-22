using Hexa.NET.ImGui;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class EditorApplication {
    const ImGuiWindowFlags PanelFlags =
        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings;

    readonly IBallisticEngineRuntime runtime;
    readonly GameWindow window;
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;
    readonly EditorCamera editorCamera = new();
    readonly EditorInput editorInput;
    readonly EditorState editorState = new();
    readonly ViewportRenderer viewport;

    readonly HierarchyPanel hierarchy;
    readonly SceneHierarchyWindow sceneHierarchy;
    readonly InspectorPanel inspector;
    readonly AssetBrowserPanel assets;

    readonly DockPanelHost extraPanels = new();

    readonly EditorPanelRegistry panels = new();

    readonly IEditorGui gui = new ImGuiEditorGui();

    readonly MaximizeController maximize = new();

    readonly EditorInputRouter inputRouter;

    readonly EditorPlayModeController playMode;
    readonly ConsolePanel console = new();
    readonly StatsPanel stats = new();
    readonly SettingsPanel settings;
    readonly TagsLayersPanel tagsLayers = new();
    readonly LayerCollisionMatrixPanel layerCollision = new();
    readonly ProfilerPanel profilerPanel;
    readonly BuildPanel buildPanel;
    readonly EditorProfilerBackend profiler;
    readonly TransformGizmo gizmo = new();
    readonly GizmoDrawer gizmoDrawer = new();

    bool showGizmos = EditorPrefs.Current.ShowGizmos;

    string maximizedPanel => maximize.Maximized;
    float contentAreaTop;
    bool maximizedViewport => maximizedPanel == EditorLayout.SceneView || maximizedPanel == EditorLayout.GameView;

    bool showStats = Environment.GetEnvironmentVariable("BALLISTIC_STATS") == "1";
    bool alwaysRefresh = EditorPrefs.Current.AlwaysRefresh;
    int forceFrames = 3;
    bool wasLoadingScene;
    Matrix4 lastCameraMatrix = Matrix4.Identity;
    SysVec2 pickPressPos;
    bool pickPressValid;
    float editorCpuMs;
    readonly System.Diagnostics.Stopwatch frameWatch = new();

    HDRenderer Renderer => RenderAsset.Current.Renderer;
    float S => imgui.Scale;

    internal static ImTextureID Tex(RenderHandle handle) => new((ulong)handle.Value);

    internal static ImTextureID Tex(nint editorTextureHandle) => new((ulong)editorTextureHandle);

    SysVec2 sceneViewSize = new(1280, 720);
    SysVec2 gameViewSize = new(1280, 720);
    SysVec2 scenePanelSize = new(1280, 720);
    SysVec2 gamePanelSize = new(1280, 720);
    readonly ViewportResolution sceneRes = new();
    readonly ViewportResolution gameRes = new();
    bool sceneViewHovered;
    bool gameViewFocused;
    bool gameViewHovered;

    bool sceneTabActive = true;

    string pendingFocusWindow = EditorLayout.SceneView;

    public EditorApplication(GameWindow window, string projectPath) {
        runtime = (IBallisticEngineRuntime)window;

        profiler = new EditorProfilerBackend(Profiler.Backend);
        Profiler.Backend = profiler;
        profilerPanel = new ProfilerPanel(profiler);

        EditorGui.Shared = gui;

        EngineBootstrap.ExtraScanAssemblies = () => {
            System.Reflection.Assembly asm = GameEditorScripts.CompileAndLoad(
                BallisticEngine.AssetPipeline.BallisticProject.Open(projectPath));
            return asm is null ? System.Array.Empty<System.Reflection.Assembly>() : [asm];
        };

        bootstrap = new EngineBootstrap(runtime, projectPath, deferAssetRefresh: true);

        DebugDraw.Enabled = true;

        imgui = new ImGuiController(window);
        editorInput = new EditorInput(window);
        inputRouter = BuildInputRouter();
        playMode = new EditorPlayModeController(
            saveBeforePlay: () => {
                if (EditorUndo.IsDirty && !string.IsNullOrEmpty(SceneCommands.CurrentScenePath))
                    SceneCommands.Save();
            },
            onEntered: () => pendingFocusWindow = EditorLayout.GameView,
            onExited: () => {
                Cursor.Mode = CursorMode.Normal;
                editorState.Selected = null;
                pendingFocusWindow = EditorLayout.SceneView;
            });
        hierarchy = new HierarchyPanel(editorState);
        sceneHierarchy = new SceneHierarchyWindow(editorState);
        inspector = new InspectorPanel(editorState);
        assets = new AssetBrowserPanel(editorState, () => imgui.Scale);
        assets.RequestScriptRebuild = RebuildScripts;
        hierarchy.CurrentAssetFolder = () => assets.CurrentFolder;

        extraPanels.Register(EditorLayout.Inspector, "Details", EditorIcons.Wrench,
            () => new InspectorPanel(editorState), p => ((InspectorPanel)p).DrawContents());
        extraPanels.Register(EditorLayout.Entities, "Entities", EditorIcons.Package,
            () => new HierarchyPanel(editorState), p => ((HierarchyPanel)p).DrawEntitiesContents());
        extraPanels.Register(EditorLayout.SceneComponents, "Scene Components", EditorIcons.World,
            () => new SceneHierarchyWindow(editorState), p => ((SceneHierarchyWindow)p).DrawSceneContents());
        extraPanels.Register(EditorLayout.Assets, "Assets", EditorIcons.Folder,
            () => new AssetBrowserPanel(editorState, () => imgui.Scale), p => ((AssetBrowserPanel)p).DrawContents());
        extraPanels.Register(EditorLayout.Console, "Console", EditorIcons.Document,
            () => new ConsolePanel(), p => ((ConsolePanel)p).DrawContents(gui));
        extraPanels.OnTitleStrip = MaximizePanelOnTitleDoubleClick;

        panels.Register(hierarchy, EditorLayout.Entities, "Entities", EditorIcons.Package);
        panels.Register(sceneHierarchy, EditorLayout.SceneComponents, "Scene Components", EditorIcons.World);
        panels.Register(inspector, EditorLayout.Inspector, "Details", EditorIcons.Wrench);
        panels.Register(assets, EditorLayout.Assets, "Assets", EditorIcons.Folder);
        panels.Register(console, EditorLayout.Console, "Console", EditorIcons.Document);
        panels.Register(EditorLayout.SceneView, "Scene View", EditorIcons.Camera, null, isViewport: true);
        panels.Register(EditorLayout.GameView, "Game View", EditorIcons.Play, null, isViewport: true);

        settings = new SettingsPanel(imgui.SetAccent, ApplyFrameRateLimit);
        buildPanel = new BuildPanel(bootstrap.Project);

        EditorWindows.Bind(ToggleWindow, OpenWindow, IsWindowOpen, IsWindowEnabled);
        EditorWindowRegistry.Rebuild();
        UserEditorWindowRegistry.Rebuild();

        ComponentPreviewRegistry.Rebuild();

        AssetInspectorRegistry.Rebuild();

        EditorLayout.SetProject(bootstrap.Project.RootPath);
        EditorLayout.Load();
        panels.ApplyHidden(EditorLayout.LoadPanelState());

        if (Environment.GetEnvironmentVariable("BALLISTIC_CURVE_WINDOW") == "1")
            CurveEditorWindow.Edit(AnimationCurve.EaseInOut(), "Verify", () => { });

        editorCamera.RestorePose(EditorPrefs.GetLastCamera(bootstrap.Project.RootPath));

        Renderer.PresentToScreen = false;

        AsyncAssetImport.AfterRefresh += PrefabPropagation.PropagateAll;

        window.FileDrop += e => ImportDroppedFiles(e.FileNames);

        window.FocusedChanged += e => {
            if (e.IsFocused) {
                scriptsRecheckPending = true;
                MarkSceneDirty();
            }
        };
        window.Minimized += e => {
            if (!e.IsMinimized)
                MarkSceneDirty();
        };
        window.Refresh += () => MarkSceneDirty();

        window.WindowState = WindowState.Maximized;
        runtime.Window.OnResizeCallback += (w, h) => {
            imgui.WindowResized(w, h);
            viewport.InvalidateTargetSizes();
        };
        imgui.WindowResized(runtime.Window.Width, runtime.Window.Height);

        this.window = window;
        viewport = new ViewportRenderer(() => Renderer);
        ApplyFrameRateLimit();

        EditorUndo.CaptureSelection = CaptureSelectionToken;
        EditorUndo.RestoreSelection = t => RestoreSelectionToken(t as SelectionToken);

        runtime.WindowUpdateCallback += OnUpdate;
        runtime.WindowRenderCallback += OnRender;

        RemotePort.Start(editorState, bootstrap);

        RemoteHandlers.FocusCamera = (center, radius, dir) => {
            if (dir.LengthSquared() > 1e-6f)
                editorCamera.LookDirection(dir);
            editorCamera.Focus(center, radius);
            forceFrames = Math.Max(forceFrames, 45);
        };
        RemoteHandlers.RequestRefresh  = () => AsyncAssetImport.Request("Refreshing assets...", forceAll: false);
        RemoteHandlers.RequestReimport = () => AsyncAssetImport.Request("Reimporting all assets...", forceAll: true);
    }

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
            return;
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
        DebugDraw.Expire();

        imgui.RefreshScale();

        editorState.ClearIfDestroyed(SceneManager.GetCurrentScene());

        if (scriptsRecheckPending) {
            scriptsRecheckPending = false;
            RebuildScripts();
            if (!AsyncAssetImport.IsBusy && AssetChangeWatch.ChangedExternally())
                AsyncAssetImport.Request("Refreshing assets...", onFinished: assets.InvalidateThumbnails);
        }

        if (Renderer is not null && Renderer.PollSurfaceReload())
            MarkSceneDirty();

        Input.Enabled = SceneManager.IsPlaying && gameViewFocused && !AsyncAssetImport.IsBusy;

        Input.PointerInGameView = gameViewHovered || runtime.Window.CursorMode == CursorMode.Locked;

        editorInput.NewFrame();
        var allowCameraInput = sceneViewHovered && !imgui.WantTextInput && !AsyncAssetImport.IsBusy;
        editorCamera.Update((float)delta, allowCameraInput, editorInput);
        MaybeSaveCameraPose((float)delta);

        Transform camT = editorCamera.Transform;
        editorState.SceneSpawnPoint = camT.Position + camT.Forward * 10f;

        HandleGlobalShortcuts();

        bootstrap.UpdateFrame(delta);

        Cursor.Apply(allowed: Input.Enabled);
    }

    void HandleGlobalShortcuts() {
        if (!editorInput.CtrlDown || imgui.WantTextInput)
            return;
        inputRouter.Dispatch(EditorInputContext.Global);
    }

    EditorInputRouter BuildInputRouter() {
        var r = new EditorInputRouter(editorInput);

        r.Bind(EditorActions.Undo, new KeyChord<Keys>(Keys.Z, ctrl: true), EditorInputContext.Global,
               () => { EditorUndo.Undo(); MarkSceneDirty(); });
        r.Bind(EditorActions.Redo, new KeyChord<Keys>(Keys.Y, ctrl: true), EditorInputContext.Global,
               () => { EditorUndo.Redo(); MarkSceneDirty(); });
        r.Bind(EditorActions.Save, new KeyChord<Keys>(Keys.S, ctrl: true), EditorInputContext.Global,
               SaveScene);
        r.Bind(EditorActions.RebuildScripts, new KeyChord<Keys>(Keys.R, ctrl: true), EditorInputContext.Global,
               RebuildScripts);

        r.Bind(EditorActions.GizmoTranslate, new KeyChord<Keys>(Keys.W), EditorInputContext.SceneViewHovered,
               () => gizmo.Mode = GizmoMode.Translate);
        r.Bind(EditorActions.GizmoRotate, new KeyChord<Keys>(Keys.E), EditorInputContext.SceneViewHovered,
               () => gizmo.Mode = GizmoMode.Rotate);
        r.Bind(EditorActions.GizmoScale, new KeyChord<Keys>(Keys.R), EditorInputContext.SceneViewHovered,
               () => gizmo.Mode = GizmoMode.Scale);

        r.Bind(EditorActions.FrameSelected, new KeyChord<Keys>(Keys.F), EditorInputContext.SceneView,
               FocusSelected);
        r.Bind(EditorActions.AlignToView, new KeyChord<Keys>(Keys.F, ctrl: true, shift: true),
               EditorInputContext.SceneView, AlignSelectedToView);
        r.Bind(EditorActions.CopyEntity, new KeyChord<Keys>(Keys.C, ctrl: true), EditorInputContext.SceneView,
               CopySelected);
        r.Bind(EditorActions.PasteEntity, new KeyChord<Keys>(Keys.V, ctrl: true), EditorInputContext.SceneView,
               PasteClipboard);

        r.Build();
        return r;
    }

    bool startupImportKicked;
    bool scriptsRecheckPending;

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

    EditorFrameGraph frameGraph;
    readonly EditorFrameContext frameContext = new();

    EditorFrameGraph BuildFrameGraph() => new EditorFrameGraph()
        .Add(new EditorDelegatePass(EditorFramePassEvent.ImportPump,     "ImportPump",     FramePassImportPump))
        .Add(new EditorDelegatePass(EditorFramePassEvent.RemotePump,     "RemotePump",     FramePassRemotePump))
        .Add(new EditorDelegatePass(EditorFramePassEvent.BuildUI,        "BuildUI",        FramePassBuildUI))
        .Add(new EditorDelegatePass(EditorFramePassEvent.StartupImport,  "StartupImport",  FramePassStartupImport))
        .Add(new EditorDelegatePass(EditorFramePassEvent.ResolveDirty,   "ResolveDirty",   FramePassResolveDirty))
        .Add(new EditorDelegatePass(EditorFramePassEvent.ViewportRender, "ViewportRender", FramePassViewportRender))
        .Add(new EditorDelegatePass(EditorFramePassEvent.ImGuiRender,    "ImGuiRender",    FramePassImGuiRender))
        .Add(new EditorDelegatePass(EditorFramePassEvent.PostPresent,    "PostPresent",    FramePassPostPresent))
        .Add(new EditorDelegatePass(EditorFramePassEvent.IdleThrottle,   "IdleThrottle",   FramePassIdleThrottle));

    void OnRender(double delta) {
        frameWatch.Restart();

        frameGraph ??= BuildFrameGraph();
        frameContext.Delta = delta;
        frameContext.RenderScene = false;
        frameGraph.Execute(frameContext);
    }

    void FramePassImportPump(EditorFrameContext ctx) => AsyncAssetImport.PumpCompletion();

    void FramePassRemotePump(EditorFrameContext ctx) => RemoteCommandQueue.Pump();

    void FramePassBuildUI(EditorFrameContext ctx) {
        using (Profiler.Zone("Editor.BuildUI")) {
            imgui.Update((float)ctx.Delta);
            BuildUI();
            BusyOverlay.Draw(S);
            BusyOverlay.DrawBakeBadge(S);
        }
    }

    void FramePassStartupImport(EditorFrameContext ctx) {
        if (!startupImportKicked) {
            startupImportKicked = true;
            AsyncAssetImport.Request("Importing project assets...", onFinished: LoadStartupScene);
        }
    }

    void FramePassResolveDirty(EditorFrameContext ctx) {
        if (editorState.ConsumeViewportDirty() || ImGui.IsAnyItemActive())
            MarkSceneDirty();

        Matrix4 camMatrix = editorCamera.Transform.WorldMatrix;
        if (camMatrix != lastCameraMatrix) {
            lastCameraMatrix = camMatrix;
            MarkSceneDirty();
        }

        if (wasLoadingScene && !SceneCommands.IsLoading)
            forceFrames = Math.Max(forceFrames, 45);
        wasLoadingScene = SceneCommands.IsLoading;

        bool activeGameUI = !sceneTabActive && BallisticEngine.UI.UIDocument.Active.Count > 0;
        ctx.RenderScene = !SceneCommands.IsLoading &&
                          (alwaysRefresh || SceneManager.IsPlaying || editorInput.RightMouseDown ||
                           gizmo.IsInteracting || forceFrames > 0 || activeGameUI);
    }

    void FramePassViewportRender(EditorFrameContext ctx) {
        if (ctx.RenderScene) {
            using var profileZone = Profiler.Zone("Editor.SceneRender");
            if (sceneTabActive)
                RenderSceneView();
            else
                RenderGameView();
            if (forceFrames > 0)
                forceFrames--;
        }

        if (SceneManager.IsPlaying)
            Coroutine.EndOfFramePump();
    }

    void FramePassImGuiRender(EditorFrameContext ctx) {
        using (Profiler.Zone("Editor.ImGuiRender"))
            imgui.Render();
    }

    void FramePassPostPresent(EditorFrameContext ctx) {
        if (SceneCommands.PumpPendingOpen()) {
            assets.InvalidateThumbnails();
            pendingFocusWindow = EditorLayout.SceneView;
            MarkSceneDirty();
        }

        editorCpuMs = editorCpuMs * 0.9f + (float)frameWatch.Elapsed.TotalMilliseconds * 0.1f;
    }

    void FramePassIdleThrottle(EditorFrameContext ctx) => UpdateIdleThrottle(ctx.RenderScene, ctx.Delta);

    const int IdleFps = 30;
    double idleSeconds;

    void UpdateIdleThrottle(bool renderedScene, double delta) {
        ImGuiIOPtr io = ImGui.GetIO();
        bool active = renderedScene || SceneManager.IsPlaying ||
                      io.WantTextInput || io.MouseDown[0] || io.MouseDown[1] || io.MouseDown[2] || Math.Abs(io.MouseDelta.X) > 0.1f || Math.Abs(io.MouseDelta.Y) > 0.1f || io.MouseWheel != 0f || ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);

        idleSeconds = active ? 0 : idleSeconds + delta;

        int userCap = EditorPrefs.Current.FrameRateLimit;
        bool throttle = idleSeconds > 0.4 && (userCap <= 0 || userCap > IdleFps);
        double targetFreq = throttle ? IdleFps : (userCap <= 0 ? 0 : userCap);
        if (Math.Abs(window.UpdateFrequency - targetFreq) > 0.5) {
            window.UpdateFrequency = targetFreq;
        }
    }

    void MarkSceneDirty() => forceFrames = 3;

    public void ApplyFrameRateLimit() {
        int limit = EditorPrefs.Current.FrameRateLimit;
        if (limit <= 0) {
            window.UpdateFrequency = 0;
        }
        else {
            window.UpdateFrequency = limit;
        }
    }

    void RenderSceneView() {
        editorCamera.SetAspect((float)Math.Max(1, (int)sceneViewSize.X) / Math.Max(1, (int)sceneViewSize.Y));
        GizmoDepthOcclusion.Enabled = EditorPrefs.Current.ShowGizmos;
        viewport.RenderSceneView(editorCamera, sceneViewSize);
    }

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

        gameCameraView.Bind(camera, (float)Math.Max(1, (int)gameViewSize.X) / Math.Max(1, (int)gameViewSize.Y));
        viewport.RenderGameView(gameCameraView, gameViewSize);
    }

    bool layoutInitialized;
    bool resetLayoutRequested;

    void BuildUI() {
        ImGuiIOPtr io = ImGui.GetIO();

        if (maximize.IsMaximized && ImGui.IsKeyPressed(ImGuiKey.Escape))
            maximize.Clear();

        maximize.DropIfUnavailable(MaximizedPanelStillAvailable);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right) ||
            ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || io.MouseWheel != 0 || ImGui.IsAnyItemActive())
            MarkSceneDirty();

        float menuH = DrawMainMenuBar();

        ImGuiViewportPtr vp = ImGui.GetMainViewport();
        float toolbarH = 44 * S;
        SysVec2 workPos = vp.WorkPos;
        SysVec2 workSize = vp.WorkSize;

        if (maximizedPanel is not null) {
            Panel("##toolbar", workPos, new SysVec2(workSize.X, toolbarH),
                PanelFlags | ImGuiWindowFlags.NoTitleBar, ToolbarUI);
            SysVec2 maxPos = workPos + new SysVec2(0, toolbarH);
            SysVec2 maxSize = new(workSize.X, workSize.Y - toolbarH);
            contentAreaTop = maxPos.Y;
            if (maximizedViewport)
                DrawMaximizedViewport(maxPos, maxSize);
            else
                DrawMaximizedPanel(maximizedPanel, maxPos, maxSize);

            DrawExitFullscreenButton(workPos, workSize, toolbarH);

            settings.DrawStandalone(gui);
            tagsLayers.DrawStandalone(gui);
            layerCollision.DrawStandalone(gui);
            profilerPanel.DrawStandalone(gui);
            buildPanel.DrawStandalone(gui);
            CurveEditorWindow.Instance.DrawStandalone(gui);
            ComponentEditorWindow.Instance.DrawStandalone(gui);
            UnityImportWindow.Instance.DrawStandalone(gui);
            UserEditorWindowRegistry.DrawAll(gui);
            DrawUnsavedPrompt();
            return;
        }

        Panel("##toolbar", workPos, new SysVec2(workSize.X, toolbarH),
            PanelFlags | ImGuiWindowFlags.NoTitleBar, ToolbarUI);

        SysVec2 hostPos = workPos + new SysVec2(0, toolbarH);
        contentAreaTop = hostPos.Y;
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

        bool buildDefault = false;
        if (!layoutInitialized) {
            layoutInitialized = true;
            buildDefault = !EditorLayout.HasSaved;
        }
        if (resetLayoutRequested) {
            resetLayoutRequested = false;
            buildDefault = true;
        }
        if (buildDefault)
            EditorLayout.BuildDefault(dockId, hostSize);

        ImGui.DockSpace(dockId, SysVec2.Zero, ImGuiDockNodeFlags.None);
        ImGui.End();

        panels.DrawCore(gui, key => pendingFocusWindow == key, MaximizePanelOnTitleDoubleClick);

        extraPanels.DrawAll();

        DrawViewportWindows();

        settings.DrawStandalone(gui);
        tagsLayers.DrawStandalone(gui);
        layerCollision.DrawStandalone(gui);
        profilerPanel.DrawStandalone(gui);
        buildPanel.DrawStandalone(gui);
        CurveEditorWindow.Instance.DrawStandalone(gui);
        ComponentEditorWindow.Instance.DrawStandalone(gui);
        UnityImportWindow.Instance.DrawStandalone(gui);
        UserEditorWindowRegistry.DrawAll(gui);
        DrawUnsavedPrompt();

        if (io.WantSaveIniSettings) {
            EditorLayout.Save();
            io.WantSaveIniSettings = false;
        }

        string hidden = string.Join('\n', panels.HiddenKeys());
        if (hidden != lastSavedPanelState) {
            EditorLayout.SavePanelState(panels.HiddenKeys());
            lastSavedPanelState = hidden;
        }
    }

    string lastSavedPanelState;

    Action pendingAfterSavePrompt;

    float DrawMainMenuBar() {
        float height = 0;
        if (!ImGui.BeginMainMenuBar())
            return height;

        height = ImGui.GetWindowSize().Y;

        if (ImGui.BeginMenu("File")) {
            if (ImGui.MenuItem($"{EditorIcons.Add}  New Scene", "Ctrl+N")) GuardUnsaved(SceneCommands.New);
            if (ImGui.MenuItem($"{EditorIcons.Save}  Save", "Ctrl+S", false, !SceneManager.IsPlaying)) SaveScene();
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

        if (ImGui.BeginMenu("Assets")) {
            if (ImGui.MenuItem($"{EditorIcons.Refresh}  Refresh", "Ctrl+R")) RebuildScripts();
            ImGui.Separator();
            DrawRegistryMenu("Assets");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window")) {
            DrawRegistryMenu("Window");

            ImGui.Separator();
            if (ImGui.BeginMenu($"{EditorIcons.Add}  Add Panel")) {
                AddTabItem(EditorLayout.Inspector, "Details");
                AddTabItem(EditorLayout.Entities, "Entities");
                AddTabItem(EditorLayout.SceneComponents, "Scene Components");
                AddTabItem(EditorLayout.Assets, "Assets");
                AddTabItem(EditorLayout.Console, "Console");
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Reset Layout")) {
                EditorLayout.DeleteSaved();
                resetLayoutRequested = true;
                panels.ResetVisibility();
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

    void DrawRegistryMenu(string topMenu) {
        int? prevOrder = null;
        foreach (EditorWindowRegistry.Entry entry in EditorWindowRegistry.Items) {
            if (entry.TopMenu != topMenu) continue;

            if (prevOrder is { } po && entry.Order - po > 10)
                ImGui.Separator();
            prevOrder = entry.Order;

            IReadOnlyList<string> subs = entry.SubMenus;
            var opened = 0;
            var skip = false;
            foreach (string sub in subs) {
                if (!ImGui.BeginMenu(sub)) { skip = true; break; }
                opened++;
            }
            if (!skip) {
                bool isToggle = EditorMenus.PathToWindowKey.TryGetValue(entry.Path, out string key);
                bool selected = isToggle && EditorWindows.IsOpen(key);
                bool enabled = !isToggle || EditorWindows.IsEnabled(key);
                if (ImGui.MenuItem(entry.Leaf, (string)null, selected, enabled))
                    entry.Invoke();
            }
            for (int i = 0; i < opened; i++)
                ImGui.EndMenu();
        }

        bool firstUser = true;
        foreach (UserEditorWindowRegistry.Entry uw in UserEditorWindowRegistry.Items) {
            int slash = uw.MenuPath.IndexOf('/');
            string uwTop = slash < 0 ? uw.MenuPath : uw.MenuPath[..slash];
            if (uwTop != topMenu) continue;

            if (firstUser) { ImGui.Separator(); firstUser = false; }

            string[] parts = uw.MenuPath.Split('/');
            string leaf = parts[^1];
            var subs = parts.Length > 2 ? parts[1..^1] : System.Array.Empty<string>();
            var opened = 0;
            var skip = false;
            foreach (string sub in subs) {
                if (!ImGui.BeginMenu(sub)) { skip = true; break; }
                opened++;
            }
            if (!skip) {
                bool selected = EditorWindows.IsOpen(uw.Key);
                if (ImGui.MenuItem(leaf, (string)null, selected, true))
                    EditorWindows.Toggle(uw.Key);
            }
            for (int i = 0; i < opened; i++)
                ImGui.EndMenu();
        }
    }

    void DrawGameObjectMenu() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.MenuItem("Create Empty")) {
            EditorCommands.Structural("Create Empty", () => editorState.Select(scene.CreateEntity("Entity")));
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
        EditorCommands.Structural($"Create {name}", () => {
            Entity entity = SceneManager.GetCurrentScene().CreateEntity(name);
            entity.AddComponent(typeof(T));
            PlaceInFrontOfCamera(entity);
            editorState.Select(entity);
        });
    }

    void CreatePrimitive(PrimitiveKind kind) {
        EditorCommands.Structural($"Create {kind}", () => {
            Entity entity = Primitives.Create(SceneManager.GetCurrentScene(), kind);
            PlaceInFrontOfCamera(entity);
            editorState.Select(entity);
            MarkSceneDirty();
        });
    }

    void PlaceInFrontOfCamera(Entity entity) {
        Transform cam = editorCamera.Transform;
        entity.transform.Position = cam.Position + cam.Forward * 6f;
    }

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
                if (SceneCommands.Save())
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

    static void Panel(string name, SysVec2 pos, SysVec2 size, ImGuiWindowFlags flags, Action contents) {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (ImGui.Begin(name, flags))
            contents();
        ImGui.End();
    }

    void ToolbarUI() {
        Scene scene = SceneManager.GetCurrentScene();

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
        UndoRedoToolbar();

        float buttonW = 46 * S;
        float gap = 6 * S;
        float groupW = buttonW * 3 + gap * 2;
        ImGui.SameLine((ImGui.GetWindowWidth() - groupW) * 0.5f);

        if (SceneManager.IsPlaying) {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.66f, 0.26f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.78f, 0.33f, 0.24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(0.85f, 0.38f, 0.27f, 1f));
            if (ImGui.Button(EditorIcons.Stop, new SysVec2(buttonW, 0)))
                playMode.ExitPlay();
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stop (exit play mode)");
        }
        else {
            var playBlockedReason = playMode.BlockedReason;

            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.16f, 0.42f, 0.24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.20f, 0.53f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(0.24f, 0.62f, 0.35f, 1f));
            ImGui.BeginDisabled(playBlockedReason is not null);
            var playClicked = ImGui.Button(EditorIcons.Play, new SysVec2(buttonW, 0));
            ImGui.EndDisabled();
            if (playBlockedReason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Play blocked: {playBlockedReason}");
            if (playClicked)
                playMode.EnterPlay();
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Play");
        }

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

        float rightBlock = 95 * S;
        ImGui.SameLine(ImGui.GetWindowWidth() - rightBlock);
        ToggleIconButton("alwaysrefresh", EditorIcons.Refresh, ref alwaysRefresh,
            "Always refresh the viewport (off = re-render only on change)");
        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(SceneManager.IsPlaying);
        if (EditorIcons.GhostButton("save", EditorIcons.Save,
                SceneManager.IsPlaying ? "Stop play to save" : "Save scene (Ctrl+S)"))
            SaveScene();
        ImGui.EndDisabled();
    }

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

        ImGui.Separator();
        ImGui.TextDisabled("Custom (0 = unlimited):");
        ImGui.SetNextItemWidth(120);
        int custom = limit;
        ImGui.InputInt("##customfps", ref custom, 5, 30);
        if (ImGui.IsItemDeactivatedAfterEdit()) {
            EditorPrefs.Current.FrameRateLimit = Math.Max(0, custom);
            ApplyFrameRateLimit();
            EditorPrefs.Save();
        }
        ImGui.EndPopup();
    }

    static void ToggleIconButton(string id, string icon, ref bool value, string tooltip) {
        SysVec4 accent = ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark];
        bool pushed = value;
        if (pushed)
            ImGui.PushStyleColor(ImGuiCol.Text, accent);
        if (EditorIcons.GhostButton(id, icon, tooltip))
            value = !value;
        if (pushed)
            ImGui.PopStyleColor();
    }

    void ResolutionBar(ViewportResolution res, string id) {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Res");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * S);
        ImGui.Combo($"##res{id}", ref res.PresetIndex,
            ViewportResolution.PresetLabels, ViewportResolution.PresetLabels.Length);

        if (res.IsCustom) {
            ImGui.SameLine(0, 6 * S);
            ImGui.SetNextItemWidth(60 * S);
            ImGui.InputInt($"##cw{id}", ref res.CustomW, 0, 0);
            ImGui.SameLine(0, 2);
            ImGui.TextDisabled("x");
            ImGui.SameLine(0, 2);
            ImGui.SetNextItemWidth(60 * S);
            ImGui.InputInt($"##ch{id}", ref res.CustomH, 0, 0);
            res.CustomW = Math.Clamp(res.CustomW, 1, 16384);
            res.CustomH = Math.Clamp(res.CustomH, 1, 16384);
        }
        else if (res.IsCustomAspect) {
            ImGui.SameLine(0, 6 * S);
            ImGui.SetNextItemWidth(46 * S);
            ImGui.InputInt($"##aw{id}", ref res.AspectW, 0, 0);
            ImGui.SameLine(0, 2);
            ImGui.TextDisabled(":");
            ImGui.SameLine(0, 2);
            ImGui.SetNextItemWidth(46 * S);
            ImGui.InputInt($"##ah{id}", ref res.AspectH, 0, 0);
            res.AspectW = Math.Clamp(res.AspectW, 1, 256);
            res.AspectH = Math.Clamp(res.AspectH, 1, 256);
        }

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

        float pad2 = ImGui.GetStyle().FramePadding.X * 2;
        float right = ImGui.GetWindowWidth() - 14 * S;
        void RightAlign(float w) { right -= w; ImGui.SameLine(right); right -= 6 * S; }

        SysVec2 rs = id == "scene" ? sceneViewSize : gameViewSize;
        var resText = $"{(int)rs.X} x {(int)rs.Y}";
        float resW = ImGui.CalcTextSize("8888 x 8888").X;
        RightAlign(resW);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(resText);

        if (id == "game") {
            var fpsLabel = $"{runtime.Window.FrameRate:0} fps {EditorIcons.ChevronDown}";
            float fpsW = ImGui.CalcTextSize($"8888 fps {EditorIcons.ChevronDown}").X + pad2;
            RightAlign(fpsW);
            DrawFpsButton(fpsLabel);
        }

        var statsLabel = $"{EditorIcons.Info} Stats";
        RightAlign(ImGui.CalcTextSize(statsLabel).X + pad2);
        ToggleIconButton($"statsbar{id}", statsLabel, ref showStats, "Statistics overlay");

        ImGui.Separator();
    }

    void DrawExitFullscreenButton(SysVec2 workPos, SysVec2 workSize, float toolbarH) {
        float margin = 8 * S;
        ImGui.SetNextWindowPos(new SysVec2(workPos.X + workSize.X * 0.5f, workPos.Y + toolbarH + margin),
            ImGuiCond.Always, new SysVec2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.9f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##exitfullscreen", flags)) {
            if (ImGui.Button($"{EditorIcons.Minimize}  Exit Fullscreen"))
                maximize.Clear();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Exit fullscreen (Esc)");
        }
        ImGui.End();
    }

    void ToggleWindow(string key) {
        if (panels.IsCorePanel(key)) {
            if (panels.Toggle(key)) pendingFocusWindow = key;
            return;
        }
        switch (key) {
            case EditorMenus.WindowKeys.Statistics: showStats = !showStats; break;
            case EditorMenus.WindowKeys.Profiler: profilerPanel.Open = !profilerPanel.Open; break;
            case EditorMenus.WindowKeys.Build: buildPanel.Open = !buildPanel.Open; break;
            case EditorMenus.WindowKeys.TagsLayers: tagsLayers.Open = !tagsLayers.Open; break;
            case EditorMenus.WindowKeys.LayerCollision: layerCollision.Open = !layerCollision.Open; break;
            case EditorMenus.WindowKeys.Settings: settings.Open = !settings.Open; break;
            default:
                if (UserEditorWindowRegistry.Get(key) is { } u) {
                    u.Window.Open = !u.Window.Open;
                    if (u.Window.Open) pendingFocusWindow = u.Window.DockKey;
                }
                break;
        }
    }

    void OpenWindow(string key) {
        if (panels.IsCorePanel(key)) {
            if (!panels.Show(key)) extraPanels.Open(key);
            return;
        }
        switch (key) {
            case EditorMenus.WindowKeys.Statistics: showStats = true; break;
            case EditorMenus.WindowKeys.Profiler: profilerPanel.Open = true; break;
            case EditorMenus.WindowKeys.Build: buildPanel.Open = true; break;
            case EditorMenus.WindowKeys.TagsLayers: tagsLayers.Open = true; break;
            case EditorMenus.WindowKeys.LayerCollision: layerCollision.Open = true; break;
            case EditorMenus.WindowKeys.Settings: settings.Open = true; break;
            case EditorMenus.WindowKeys.UnityImport: UnityImportWindow.Show(); break;
            default:
                if (UserEditorWindowRegistry.Get(key) is { } u) {
                    u.Window.Open = true;
                    pendingFocusWindow = u.Window.DockKey;
                }
                break;
        }
    }

    bool IsWindowOpen(string key) {
        if (panels.IsCorePanel(key)) return panels.IsShown(key);
        return key switch {
            EditorMenus.WindowKeys.Statistics => showStats,
            EditorMenus.WindowKeys.Profiler => profilerPanel.Open,
            EditorMenus.WindowKeys.Build => buildPanel.Open,
            EditorMenus.WindowKeys.TagsLayers => tagsLayers.Open,
            EditorMenus.WindowKeys.LayerCollision => layerCollision.Open,
            EditorMenus.WindowKeys.Settings => settings.Open,
            EditorMenus.WindowKeys.UnityImport => UnityImportWindow.IsOpen,
            _ => UserEditorWindowRegistry.Get(key)?.Window.Open ?? false,
        };
    }

    bool IsWindowEnabled(string key) => true;

    void MaximizePanelOnTitleDoubleClick(string panelName) {
        SysVec2 mouse = ImGui.GetIO().MousePos;
        SysVec2 winPos = ImGui.GetWindowPos();
        float winW = ImGui.GetWindowSize().X;
        float contentTop = winPos.Y + ImGui.GetCursorStartPos().Y;
        float stripTop = winPos.Y - (ImGui.IsWindowDocked() ? ImGui.GetFrameHeight() : 0f);
        stripTop = Math.Max(stripTop, contentAreaTop);
        bool onStrip = mouse.Y >= stripTop && mouse.Y < contentTop &&
                       mouse.X >= winPos.X && mouse.X <= winPos.X + winW;

        if (onStrip && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            maximize.Toggle(panelName);

        if (onStrip && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"##tabctx_{panelName}");
        if (ImGui.BeginPopup($"##tabctx_{panelName}")) {
            if (ImGui.BeginMenu($"{EditorIcons.Add}  Add Tab")) {
                AddTabItem(EditorLayout.Inspector, "Details");
                AddTabItem(EditorLayout.Entities, "Entities");
                AddTabItem(EditorLayout.SceneComponents, "Scene Components");
                AddTabItem(EditorLayout.Assets, "Assets");
                AddTabItem(EditorLayout.Console, "Console");
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Maximize / Restore", "Double-click"))
                maximize.Toggle(panelName);
            ImGui.EndPopup();
        }
    }

    void AddTabItem(string kindKey, string label) {
        int total = (panels.IsShown(kindKey) ? 1 : 0) + extraPanels.CountOf(kindKey);
        string hint = total > 0 ? $"{total} open" : null;
        if (ImGui.MenuItem(label, hint)) {
            if (!panels.Show(kindKey)) extraPanels.Open(kindKey);
        }
    }

    bool MaximizedPanelStillAvailable(string name) {
        if (panels.Contains(name)) return panels.IsAvailable(name);
        return extraPanels.OwnsLabel(name);
    }

    void DrawMaximizedPanel(string name, SysVec2 pos, SysVec2 size) {
        if (extraPanels.OwnsLabel(name)) {
            if (extraPanels.DrawMaximizedInstance(name, pos, size, MaximizePanelOnTitleDoubleClick))
                maximize.Clear();
            return;
        }

        EditorPanelRegistry.Descriptor d = panels.Get(name);
        if (d?.Window is null) {
            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);
            const ImGuiWindowFlags emptyFlags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
            if (ImGui.Begin($"{name}###maxpanel", emptyFlags)) {
                ImGui.TextDisabled("This panel can't be shown fullscreen.");
                if (ImGui.Button("Exit Fullscreen")) maximize.Clear();
            }
            ImGui.End();
            return;
        }

        bool open = WindowShell.DrawMaximized(d.Window, gui, pos, size, MaximizePanelOnTitleDoubleClick);
        if (!open) {
            panels.SetShown(name, false);
            maximize.Clear();
        }
    }

    void DrawViewportWindows() {
        gameViewFocused = false;
        gameViewHovered = false;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(1, 1));

        if (pendingFocusWindow == EditorLayout.SceneView) ImGui.SetNextWindowFocus();
        if (ImGui.Begin(EditorLayout.SceneView)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.SceneView);
            if (ImGui.IsWindowFocused()) sceneTabActive = true;
            SceneTabContents();
        }
        ImGui.End();

        if (pendingFocusWindow == EditorLayout.GameView) ImGui.SetNextWindowFocus();
        if (ImGui.Begin(EditorLayout.GameView)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.GameView);
            if (ImGui.IsWindowFocused()) sceneTabActive = false;
            GameTabContents();
        }
        ImGui.End();

        pendingFocusWindow = null;
        ImGui.PopStyleVar();
    }

    void DrawMaximizedViewport(SysVec2 pos, SysVec2 size) {
        gameViewFocused = false;
        gameViewHovered = false;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(1, 1));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (ImGui.Begin("##viewportmax", PanelFlags | ImGuiWindowFlags.NoTitleBar)) {
            bool sceneMax = maximizedPanel == EditorLayout.SceneView;
            sceneTabActive = sceneMax;
            if (sceneMax)
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

        (SysVec2 uv0, SysVec2 uv1) = sceneRes.ZoomUVs(!Renderer.DisplayTextureTopDown);
        ImGui.Image(Tex(Renderer.SceneColorHandle), dispSize, uv0, uv1);
        SysVec2 imageMin = ImGui.GetItemRectMin();
        SysVec2 imageSize = dispSize;

        if (ImGui.BeginDragDropTarget()) {
            if (hierarchy.DropAssetsIntoScene())
                MarkSceneDirty();
            ImGui.EndDragDropTarget();
        }

        ImGui.GetWindowDrawList().AddRect(imageMin, imageMin + imageSize,
            ImGui.GetColorU32(new SysVec4(1, 1, 1, 0.06f)));
        sceneViewHovered = ImGui.IsItemHovered();
        gameViewFocused = false;
        gameViewHovered = false;

        DrawSceneViewToolbar(imageMin, imageSize);

        if (sceneViewHovered && !editorInput.RightMouseDown)
            inputRouter.Dispatch(EditorInputContext.SceneViewHovered);
        if (!editorInput.RightMouseDown && !imgui.WantTextInput)
            inputRouter.Dispatch(EditorInputContext.SceneView);

        float zoom = Math.Max(1f, sceneRes.Zoom);
        SysVec2 center = imageMin + imageSize * 0.5f;
        SysVec2 gizmoSize = imageSize * zoom;
        SysVec2 gizmoMin = center - gizmoSize * 0.5f;

        if (EditorPrefs.Current.ShowGrid)
            ViewportGrid.Draw(editorCamera, gizmoMin, gizmoSize, EditorPrefs.Current.GridSize);

        if (showGizmos)
            DrawComponentGizmos(gizmoMin, gizmoSize);

        VertexSnap.Held = sceneViewHovered && !imgui.WantTextInput && editorInput.KeyDown(Keys.V);
        if (VertexSnap.Held)
            MarkSceneDirty();

        if (TerrainTool.Armed && sceneViewHovered)
            MarkSceneDirty();

        if (editorState.Selected is not null)
            gizmo.Draw(editorCamera, editorState.Selected, gizmoMin, gizmoSize,
                sceneViewHovered && !ColliderHandles.IsInteracting && !WheelHandles.IsInteracting && !TerrainTool.Armed);

        HandleScenePick(gizmoMin, gizmoSize);

        DrawOrientationGizmo(imageMin, imageSize);

        if (showStats && !stats.Draw(runtime.Window.FrameRate, editorCpuMs, sceneViewSize, S,
                imageMin, imageSize, 105 * S, RenderStats.Scene, showTiming: false))
            showStats = false;
    }

    void DrawOrientationGizmo(SysVec2 imageMin, SysVec2 imageSize) {
        OrientationGizmo.Draw(editorCamera, imageMin, imageSize, S, sceneViewHovered);
    }

    void DrawComponentGizmos(SysVec2 imageMin, SysVec2 imageSize) {
        gizmoDrawer.Begin(editorCamera, imageMin, imageSize, ImGui.GetWindowDrawList());

        foreach (DebugDraw.Segment segment in DebugDraw.Segments) {
            gizmoDrawer.Color = segment.Color;
            gizmoDrawer.DrawLine(segment.From, segment.To);
        }
        gizmoDrawer.Color = Vector3.One;

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

            foreach (Behaviour behaviour in selected.Behaviours)
                if (behaviour is Collider collider &&
                    ColliderHandles.Draw(collider, editorCamera, imageMin, imageSize,
                        ImGui.GetWindowDrawList(), sceneViewHovered && !gizmo.IsInteracting))
                    MarkSceneDirty();

            foreach (Behaviour behaviour in selected.Behaviours)
                if (behaviour is WheelCollider wheel &&
                    WheelHandles.Draw(wheel, editorCamera, imageMin, imageSize,
                        ImGui.GetWindowDrawList(), sceneViewHovered && !gizmo.IsInteracting && !ColliderHandles.IsInteracting))
                    MarkSceneDirty();

            Terrain selectedTerrain = selected.GetComponent<Terrain>();
            if (selectedTerrain is null)
                TerrainTool.Armed = false;
            else if (TerrainTool.Draw(selectedTerrain, editorCamera, imageMin, imageSize,
                         ImGui.GetWindowDrawList(),
                         sceneViewHovered && !gizmo.IsInteracting && !ColliderHandles.IsInteracting))
                MarkSceneDirty();
        }
        else {
            TerrainTool.Armed = false;
        }

        if (editorState.SelectedSceneBehaviour is { } selectedSceneBehaviour) {
            try { selectedSceneBehaviour.OnDrawGizmosSelected(gizmoDrawer); }
            catch (Exception e) { ScriptGuard.ReportRepeating(selectedSceneBehaviour, "OnDrawGizmosSelected", e); }
        }
    }

    void HandleScenePick(SysVec2 viewMin, SysVec2 viewSize) {
        bool gizmoBusy = gizmo.IsInteracting || gizmo.IsHovered ||
                         ColliderHandles.IsInteracting || WheelHandles.IsInteracting || VertexSnap.Held ||
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

        const float clickSlop = 4f;
        if (SysVec2.Distance(ImGui.GetMousePos(), pickPressPos) > clickSlop)
            return;

        Matrix4 vp = editorCamera.GetViewMatrix() * editorCamera.GetProjectionMatrix();
        Entity hit = ScenePicker.Pick(vp, viewMin, viewSize, ImGui.GetMousePos());

        if (!ReferenceEquals(hit, editorState.Selected)) {
            if (hit is not null)
                editorState.Select(hit);
            else
                editorState.Selected = null;
            MarkSceneDirty();
        }
    }

    void FocusSelected() {
        Entity selected = editorState.Selected;
        if (selected is null)
            return;

        if (EditorBounds.TryGetWorldBounds(selected, out Vector3 center, out float radius))
            editorCamera.Focus(center, radius);
        else
            editorCamera.Focus(selected.transform.WorldPosition, 1f);
    }

    void AlignSelectedToView() {
        Entity selected = editorState.Selected;
        if (selected is null)
            return;

        Transform cam = editorCamera.Transform;
        EditorCommands.EditEntity(selected, "Align To View", () => {
            selected.transform.Position = cam.Position;
            selected.transform.Rotation = cam.Rotation;
            MarkSceneDirty();
        });
    }

    void CopySelected() {
        if (editorState.Selected is { } selected)
            EditorClipboard.Copy(selected);
    }

    void PasteClipboard() {
        if (!EditorClipboard.HasCopy)
            return;

        EditorCommands.Structural("Paste", () => {
            if (EditorClipboard.Paste(SceneManager.GetCurrentScene()) is { } copy) {
                editorState.Select(copy);
                MarkSceneDirty();
            }
        });
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

            (SysVec2 uv0, SysVec2 uv1) = gameRes.ZoomUVs(!Renderer.DisplayTextureTopDown);
            ImGui.Image(Tex(Renderer.GameColorHandle), dispSize, uv0, uv1);
            gameViewFocused = ImGui.IsWindowFocused();
            gameViewHovered = ImGui.IsItemHovered();

            if (Input.Enabled) {
                SysVec2 imgMin = ImGui.GetItemRectMin();
                var panelRect = new BallisticEngine.UI.Rect(imgMin.X, imgMin.Y, dispSize.X, dispSize.Y);
                foreach (var doc in BallisticEngine.UI.UIDocument.Active)
                    doc.ProcessInput(panelRect);
            }

            if (showStats && !stats.Draw(runtime.Window.FrameRate, editorCpuMs, gameViewSize, S,
                    ImGui.GetItemRectMin(), dispSize, 10 * S, RenderStats.Game, showTiming: true))
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

    void ImportDroppedFiles(IReadOnlyList<string> files) {
        if (files is null || files.Count == 0) {
            Debugging.LogWarning("Drop import: the OS reported no files.");
            return;
        }

        var destFolder = Path.Combine(bootstrap.Project.RootPath,
            assets.CurrentFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destFolder);

        var copied = 0;
        foreach (var source in files) {
            try {
                if (Directory.Exists(source)) {
                    CopyDirectoryInto(source, destFolder);
                    copied++;
                    continue;
                }
                if (!File.Exists(source)) {
                    Debugging.LogWarning($"Drop import: '{source}' is neither a file nor a folder; skipped.");
                    continue;
                }

                string destination = UniqueDropPath(Path.Combine(destFolder, Path.GetFileName(source)));
                File.Copy(source, destination);
                copied++;
            }
            catch (Exception e) {
                Debugging.LogError($"Drop import failed for '{source}': {e.Message}");
            }
        }

        if (copied > 0)
            AsyncAssetImport.Request(
                copied == 1 ? $"Importing {Path.GetFileName(files[0])}..." : $"Importing {copied} items...",
                onFinished: () => assets.InvalidateThumbnails());
    }

    static string UniqueDropPath(string path) {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path),
               ext = Path.GetExtension(path);
        for (int i = 1; ; i++) {
            string candidate = Path.Combine(dir, $"{stem} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    static void CopyDirectoryInto(string sourceDir, string destParent) {
        string dest = Path.Combine(destParent, Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (string sub in Directory.GetDirectories(sourceDir))
            CopyDirectoryInto(sub, dest);
    }


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

    void DrawSceneViewToolbar(SysVec2 imageMin, SysVec2 imageSize) {
        float margin = EditorTheme.OverlayMargin * S;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.PushStyleColor(ImGuiCol.WindowBg, EditorTheme.OverlayBg);
        ImGui.PushStyleColor(ImGuiCol.Border, EditorTheme.OverlayBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, EditorTheme.OverlayRounding * S);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(8 * S, 6 * S));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new SysVec2(6 * S, 6 * S));

        ImGui.SetNextWindowPos(new SysVec2(imageMin.X + margin, imageMin.Y + margin), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(EditorTheme.OverlayBg.W);
        if (ImGui.Begin("##sceneToolsOverlay", flags)) {
            string lMove = EditorIcons.Add + " Move", lRot = EditorIcons.Refresh + " Rotate",
                   lScale = EditorIcons.Maximize + " Scale";
            float framePadX = ImGui.GetStyle().FramePadding.X * 2f;
            float bw = MathF.Max(58 * S, MathF.Max(ImGui.CalcTextSize(lMove).X,
                MathF.Max(ImGui.CalcTextSize(lRot).X, ImGui.CalcTextSize(lScale).X)) + framePadX);
            float h = ImGui.GetFrameHeight();
            SysVec2 pillStart = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(
                pillStart - new SysVec2(3 * S, 3 * S),
                pillStart + new SysVec2(bw * 3 + 4 * S + 3 * S, h + 3 * S),
                ImGui.GetColorU32(EditorTheme.OverlayPill), 6f * S);
            GizmoModeButton(lMove, GizmoMode.Translate, bw, "Move (W)");
            ImGui.SameLine(0, 2 * S);
            GizmoModeButton(lRot, GizmoMode.Rotate, bw, "Rotate (E)");
            ImGui.SameLine(0, 2 * S);
            GizmoModeButton(lScale, GizmoMode.Scale, bw, "Scale (R)");

            ImGui.SameLine(0, 10 * S);
            bool isPivot = gizmo.Pivot == GizmoPivot.Pivot;
            float pivotW = MathF.Max(58 * S,
                MathF.Max(ImGui.CalcTextSize("Pivot").X, ImGui.CalcTextSize("Center").X) + framePadX);
            if (ImGui.Button(isPivot ? "Pivot" : "Center", new SysVec2(pivotW, h)))
                gizmo.Pivot = isPivot ? GizmoPivot.Center : GizmoPivot.Pivot;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isPivot ? "Handle at the entity's pivot (click for Center)"
                                         : "Handle at the selection's center (click for Pivot)");

            ImGui.SameLine(0, 6 * S);
            bool world = gizmo.Space == GizmoSpace.World;
            string spaceIcon = world ? EditorIcons.World : EditorIcons.Package;
            if (EditorIcons.GhostButton("ovgizmospace", spaceIcon,
                    world ? "Gizmo space: World (click for Local)" : "Gizmo space: Local (click for World)"))
                gizmo.Space = world ? GizmoSpace.Local : GizmoSpace.World;

            ImGui.SameLine(0, 8 * S);
            ImGui.AlignTextToFramePadding();
            bool snapOn = ImGui.GetIO().KeyCtrl;
            if (snapOn) ImGui.TextColored(EditorPrefs.Current.Accent, $"{EditorIcons.Grid} Snap");
            else { ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim); ImGui.Text($"{EditorIcons.Grid} Snap"); ImGui.PopStyleColor(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Ctrl while dragging a gizmo to snap.");
        }
        ImGui.End();

        const float gizmoBottom = (34f + 14f) + (34f + 8f);
        float eyeMenuY = imageMin.Y + gizmoBottom * S + margin;
        EditorPrefs prefs = EditorPrefs.Current;
        ImGui.SetNextWindowPos(new SysVec2(imageMin.X + imageSize.X - margin, eyeMenuY),
            ImGuiCond.Always, new SysVec2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(EditorTheme.OverlayBg.W);
        if (ImGui.Begin("##sceneVisibilityOverlay", flags)) {
            if (EditorIcons.GhostButton("ovvisibility", $"{EditorIcons.Eye} {EditorIcons.ChevronDown}",
                    "Visibility: grid, gizmos, GI-debug overlays"))
                ImGui.OpenPopup("##visibilitymenu");
            if (ImGui.BeginPopup("##visibilitymenu")) {
                ImGui.TextDisabled("Show in Scene");
                ImGui.Separator();

                bool grid = prefs.ShowGrid;
                if (ImGui.MenuItem($"{EditorIcons.Grid}  Grid", (string)null, grid)) {
                    prefs.ShowGrid = !grid; EditorPrefs.Save();
                }
                bool giz = showGizmos;
                if (ImGui.MenuItem($"{EditorIcons.Pin}  Component Gizmos", (string)null, giz)) {
                    showGizmos = !giz; prefs.ShowGizmos = showGizmos; EditorPrefs.Save();
                }

                ImGui.EndPopup();
            }
        }
        ImGui.End();

        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
    }

    void LoadStartupScene() {
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

    void RebuildScripts() {
        if (AsyncAssetImport.IsBusy || SceneCommands.IsLoading)
            return;

        if (bootstrap.ReloadGameScripts())
            MarkSceneDirty();
    }
}
