using BallisticEngine.Serialization;
using Hexa.NET.ImGui;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
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
    readonly GameWindow window;   // the windowed DX12 host (GameWindow + IBallisticEngineRuntime + IWindow)
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;
    readonly EditorCamera editorCamera = new();
    readonly EditorInput editorInput;
    readonly EditorState editorState = new();
    readonly ViewportRenderer viewport;   // single source for the Scene/Game offscreen render sequence

    readonly HierarchyPanel hierarchy;
    readonly InspectorPanel inspector;
    readonly AssetBrowserPanel assets;

    // EXTRA panel instances beyond the primary docked ones: the user can open as many Inspector /
    // Entities / Assets / Console / Scene-component tabs as they want (Add Tab menu). The primary
    // panels above stay as fields (lots of code references them); the host owns the duplicates.
    readonly DockPanelHost extraPanels = new();
    // A1b: the single descriptor table for the CORE dockable panels (one declaration per panel that the
    // normal draw, the maximize content path, and the maximize-availability check all read).
    readonly EditorPanelRegistry panels = new();
    // A1b: the maximize state machine (which panel fills the window) — replaces the bare `maximizedPanel`
    // string + its three hand-synced clear sites with one can't-get-stuck controller.
    readonly MaximizeController maximize = new();
    // A4: the editor's hotkeys as one declarative, priority-resolved, conflict-checkable table (the input half
    // of the shell). The scattered inline ImGui.IsKeyPressed/editorInput.KeyPressed guards across OnUpdate +
    // BuildUI now route through this — each binding declares its context so a chord can't leak between scopes.
    readonly EditorInputRouter inputRouter;
    // A5: the play/edit mode controller (the LAST Phase-A item). The editor-side wrapper around the
    // engine's play lifecycle (SceneManager.StartPlay/StopPlay) — the mode-TRANSITION side-effects
    // (save-guard before play, cursor reset + selection clear + window focus on stop) that were inline
    // in the toolbar's Play/Stop button bodies now live in one explicit enter/exit-hook controller.
    readonly EditorPlayModeController playMode;
    readonly ConsolePanel console = new();
    readonly StatsPanel stats = new();
    readonly SettingsPanel settings;
    readonly TagsLayersPanel tagsLayers = new();
    readonly LayerCollisionMatrixPanel layerCollision = new();   // EF8: matrix split into its own window
    readonly ProfilerPanel profilerPanel = new();
    readonly BuildPanel buildPanel;
    readonly EditorProfilerBackend profiler;
    readonly TransformGizmo gizmo = new();
    readonly GizmoDrawer gizmoDrawer = new();

    bool showGizmos = EditorPrefs.Current.ShowGizmos;  // component gizmos in the Scene view

    // A1b-deeper: the five core panels' visibility (Entities / Scene-components / Inspector / Assets /
    // Console) lives in the `panels` registry now (one Shown per descriptor) — the old showHierarchy/
    // showSceneComponents/showInspector/showBottom/showConsole fields are gone. EditorApplication names
    // no core panel: it toggles/opens/queries them BY KEY through the registry.
    // Double-click ANY panel's tab to fill the window with it; Esc restores. null = no panel
    // maximized. (Was viewport-only; now works for every dockable panel.) A1b: the backing state now
    // lives in `maximize` (MaximizeController) — the maximized key is single-sourced there. Reads route
    // through this convenience accessor; mutations go through maximize.Toggle/Clear (the can't-forget API).
    string maximizedPanel => maximize.Maximized;
    float contentAreaTop;   // Y of the dock area's top (just under the toolbar); clamps the tab-strip band
    bool maximizedViewport => maximizedPanel == EditorLayout.SceneView || maximizedPanel == EditorLayout.GameView;

    bool showStats = Environment.GetEnvironmentVariable("BALLISTIC_STATS") == "1"; // auto-open for agents/CI
    bool alwaysRefresh = EditorPrefs.Current.AlwaysRefresh;   // off = re-render only on change
    int forceFrames = 3;
    bool wasLoadingScene;   // falling edge -> burst frames so auto-exposure converges after a load
    Matrix4 lastCameraMatrix = Matrix4.Identity;   // previous frame's editor-camera pose (idle-render trigger)
    SysVec2 pickPressPos;        // where LMB went down in the viewport (click-vs-drag test for picking)
    bool pickPressValid;         // the press began as a candidate select-click (not on a gizmo/handle)
    float editorCpuMs;
    readonly System.Diagnostics.Stopwatch frameWatch = new();

    HDRenderer Renderer => RenderAsset.Current.Renderer;
    float S => imgui.Scale;

    // GL texture name -> ImGui texture handle. Hexa's ImGui.Image/ImageButton take an ImTextureID
    // (u64 handle), with no implicit int conversion, so every raw GL texture id routes through here.
    internal static ImTextureID Tex(RenderHandle handle) => new((ulong)handle.Value);
    // Overload for the editor's own preview/thumbnail textures: a GL texture name (GL backend) or a DX12
    // UiHeap GPU descriptor ptr (DX12). nint holds both; the active ImGui backend interprets it.
    internal static ImTextureID Tex(nint editorTextureHandle) => new((ulong)editorTextureHandle);

    SysVec2 sceneViewSize = new(1280, 720);   // render resolution of the Scene offscreen target
    SysVec2 gameViewSize = new(1280, 720);     // render resolution of the Game offscreen target
    SysVec2 scenePanelSize = new(1280, 720);   // on-screen panel area available for the Scene view
    SysVec2 gamePanelSize = new(1280, 720);
    readonly ViewportResolution sceneRes = new();
    readonly ViewportResolution gameRes = new();
    bool sceneViewHovered;
    bool gameViewFocused;
    bool gameViewHovered;   // mouse is over the Game view image (used to gate click-to-recapture)
    bool sceneTabActive = true;
    // A window name to focus next frame (play → Game View, stop → Scene View). Now that Scene/Game are
    // separate dockable windows, "select tab" means focus that window so it surfaces above its dock node.
    string pendingFocusWindow = EditorLayout.SceneView;

    public EditorApplication(GameWindow window, string projectPath) {
        runtime = (IBallisticEngineRuntime)window;

        // Record every main-thread zone for the Profiler panel, forwarding to Tracy if
        // Program.cs installed it (BALLISTIC_TRACY=1).
        profiler = new EditorProfilerBackend(Profiler.Backend);
        Profiler.Backend = profiler;

        // Defer the (slow) asset import: bring the window up first, then refresh asynchronously behind
        // the busy overlay. The startup scene loads once that first import completes (see OnRender).
        bootstrap = new EngineBootstrap(runtime, projectPath, deferAssetRefresh: true);

        // The editor consumes runtime debug lines (Debug.DrawLine/DrawRay) via the gizmo drawer;
        // turning this on makes the engine-side buffer actually record (a shipped player leaves it
        // off so release play pays nothing).
        DebugDraw.Enabled = true;

        imgui = new ImGuiController(window);
        editorInput = new EditorInput(window);
        inputRouter = BuildInputRouter();
        // A5: the mode-transition side-effects, supplied as the controller's enter/exit handlers. The
        // handlers capture `this`/`editorState` and only fire at transition time (well after ctor), so
        // referencing fields here is safe. Behaviour is byte-identical to the old inline toolbar bodies.
        playMode = new EditorPlayModeController(
            saveBeforePlay: () => {
                // Persist edits to disk before play (Unity-style): play mode only keeps an in-memory
                // snapshot that Stop restores, so a close/crash mid-play would otherwise lose unsaved
                // edits (collider sizes, etc.). Only when there's something to save and a file to save to.
                if (EditorUndo.IsDirty && !string.IsNullOrEmpty(SceneCommands.CurrentScenePath))
                    SceneCommands.Save();
            },
            onEntered: () => pendingFocusWindow = EditorLayout.GameView,
            onExited: () => {
                Cursor.Mode = CursorMode.Normal; // clear any leftover lock intent from the play session
                editorState.Selected = null;
                pendingFocusWindow = EditorLayout.SceneView;
            });
        hierarchy = new HierarchyPanel(editorState);
        inspector = new InspectorPanel(editorState);
        assets = new AssetBrowserPanel(editorState, () => imgui.Scale);
        assets.RequestScriptRebuild = RebuildScripts;
        hierarchy.CurrentAssetFolder = () => assets.CurrentFolder;

        // Register the duplicable panel kinds. The factory makes a FRESH instance (own lock/folder
        // state); the draw delegate routes to its content method. The primary docked panels (the
        // fields above) are id-0; the Add Tab menu opens extras through the host.
        // EF12: the Inspector panel is presented to the user as "Details" (Unity-style). The registry KEY
        // stays EditorLayout.Inspector (= the dock-.ini / .panels-sidecar id); only the DISPLAY title changes.
        extraPanels.Register(EditorLayout.Inspector, "Details", EditorIcons.Wrench,
            () => new InspectorPanel(editorState), p => ((InspectorPanel)p).DrawContents());
        extraPanels.Register(EditorLayout.Entities, "Entities", EditorIcons.Package,
            () => new HierarchyPanel(editorState), p => ((HierarchyPanel)p).DrawEntitiesContents());
        extraPanels.Register(EditorLayout.SceneComponents, "Scene Components", EditorIcons.World,
            () => new HierarchyPanel(editorState), p => ((HierarchyPanel)p).DrawSceneContents());
        extraPanels.Register(EditorLayout.Assets, "Assets", EditorIcons.Folder,
            () => new AssetBrowserPanel(editorState, () => imgui.Scale), p => ((AssetBrowserPanel)p).DrawContents());
        extraPanels.Register(EditorLayout.Console, "Console", EditorIcons.Document,
            () => new ConsolePanel(), p => ((ConsolePanel)p).DrawContents());
        extraPanels.OnTitleStrip = MaximizePanelOnTitleDoubleClick;

        // A1b-deeper: declare the CORE dockable panels ONCE in the registry, which now OWNS their show
        // state (the five showXxx bools are gone — the registry's per-descriptor Shown is the single
        // source). The normal docked draw (DrawCore), the maximize content path, the maximize-availability
        // check, and the Window-menu toggle/open/checkmark all read these descriptors — no hand-synced
        // if/else chain + still-available switch + showXxx triple + the five per-name menu switches.
        // DrawContents routes to the same primary panel field the normal docked path uses (the maximized
        // view and the docked view share one instance/state). The two viewports are flagged IsViewport
        // (their fullscreen draw is the render-target compositing path in DrawMaximizedViewport, not a
        // generic body) and are always available; registration order == the old hardcoded draw order.
        panels.Register(EditorLayout.Entities, "Entities", EditorIcons.Package, hierarchy.DrawEntitiesContents);
        panels.Register(EditorLayout.SceneComponents, "Scene Components", EditorIcons.World, hierarchy.DrawSceneContents);
        panels.Register(EditorLayout.Inspector, "Details", EditorIcons.Wrench, inspector.DrawContents);  // EF12: KEY stays "Inspector", display = "Details"
        panels.Register(EditorLayout.Assets, "Assets", EditorIcons.Folder, assets.DrawContents);
        panels.Register(EditorLayout.Console, "Console", EditorIcons.Document, console.DrawContents);
        panels.Register(EditorLayout.SceneView, "Scene View", EditorIcons.Camera, null, isViewport: true);
        panels.Register(EditorLayout.GameView, "Game View", EditorIcons.Play, null, isViewport: true);

        // Wire the editor-only extra debug views (AO / Lit / Luminance) into the renderer's hook.
        EditorDebugViews.Install();
        settings = new SettingsPanel(imgui.SetAccent, ApplyFrameRateLimit);
        buildPanel = new BuildPanel(bootstrap.Project);

        // A1 (Rule 3): bind the static EditorWindows facade to this instance so the self-registered
        // [MenuItem] window commands (discovered by EditorWindowRegistry) act on the live editor. The menu
        // bar is built from the registry — EditorApplication no longer names windows when drawing the menu.
        // Build the registry once now so the first menu draw doesn't pay the reflection scan (TypeCache is
        // already built by EngineBootstrap above).
        EditorWindows.Bind(ToggleWindow, OpenWindow, IsWindowOpen, IsWindowEnabled);
        EditorWindowRegistry.Rebuild();

        // B1 (Rule 1): warm the component-preview registry the same way, so the first inspector draw doesn't
        // pay the [ComponentPreview] reflection scan. The inspector resolves custom sections from this registry
        // by type instead of the old `if (behaviour is Renderer/Volume/...)` instanceof chain.
        ComponentPreviewRegistry.Rebuild();

        // B2 (Rule 1): warm the asset-inspector registry the same way, so the first asset selection doesn't
        // pay the [AssetInspector] reflection scan. DrawAssetInspector resolves the per-extension body from
        // this registry instead of the old `switch (ext) { case ".mat": ... }` god-switch.
        AssetInspectorRegistry.Rebuild();

        // Per-project dock layout: key by the project root, then apply the saved arrangement before the
        // first frame (BuildUI lays out the default if none exists).
        EditorLayout.SetProject(bootstrap.Project.RootPath);
        EditorLayout.Load();
        // EF9c: the .ini restores each panel's geometry/dock node but not whether it's open. Re-apply the
        // persisted closed-panel set so a panel the user closed last session stays closed across restart.
        panels.ApplyHidden(EditorLayout.LoadPanelState());

        // Restore the Scene-view camera to wherever it was last left in this project.
        editorCamera.RestorePose(EditorPrefs.GetLastCamera(bootstrap.Project.RootPath));

        Renderer.PresentToScreen = false;

        // After any asset refresh, propagate .prefab edits into live prefab instances (overrides
        // preserved). Idempotent: a refresh that didn't change a prefab rebuilds nothing.
        AsyncAssetImport.AfterRefresh += PrefabPropagation.PropagateAll;

        // Files dragged from the OS onto the editor window import into the browser's folder.
        window.FileDrop += e => ImportDroppedFiles(e.FileNames);

        // Unity-style auto-compile: regaining window focus (back from the IDE after editing a
        // script) re-checks the sources on the next update tick. The up-to-date fast path in
        // GameScripts makes this a cheap mtime scan when nothing changed.
        window.FocusedChanged += e => {
            if (e.IsFocused) {
                scriptsRecheckPending = true;
                // Force a full repaint on focus regain. With on-demand rendering the scene view is a
                // cached offscreen texture; after an alt-tab (or minimise/restore) nothing is dirty,
                // the camera hasn't moved and forceFrames is 0, so the scene FBO never re-renders and
                // the whole present can come back BLACK (a stale/lost backbuffer on Windows). Re-arming
                // forceFrames repaints the scene texture AND the backbuffer for the next few frames.
                MarkSceneDirty();
            }
        };
        // A minimise/restore can also drop the surface without a focus toggle (e.g. restored by
        // clicking the taskbar while already "focused"); repaint on un-minimise too.
        window.Minimized += e => {
            if (!e.IsMinimized)
                MarkSceneDirty();
        };
        // GLFW raises Refresh whenever the OS says the window's contents need redrawing (uncovered,
        // restored, moved between monitors). This is the canonical "your backbuffer is stale, repaint"
        // signal — exactly the alt-tab-return case — so honour it directly.
        window.Refresh += () => MarkSceneDirty();

        window.WindowState = WindowState.Maximized;
        runtime.Window.OnResizeCallback += (w, h) => {
            imgui.WindowResized(w, h);
            viewport.InvalidateTargetSizes(); // re-sync offscreen targets next frame
        };
        imgui.WindowResized(runtime.Window.Width, runtime.Window.Height);

        this.window = window;
        viewport = new ViewportRenderer(() => Renderer);
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

        // Let the command port frame the Scene-view fly camera (the screenshot captures THAT view,
        // not an HDCamera entity) — so an agent can position a shot. Runs on the main thread already.
        RemoteHandlers.FocusCamera = (center, radius, dir) => {
            if (dir.LengthSquared() > 1e-6f)
                editorCamera.LookDirection(dir);     // reorient first (e.g. 3/4 top view)
            editorCamera.Focus(center, radius);
            forceFrames = Math.Max(forceFrames, 45);  // burst so auto-exposure re-meters for the new view
        };
        RemoteHandlers.RequestRefresh = () => AsyncAssetImport.Request("Refreshing assets...", forceAll: true);
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
        Input.PointerInGameView = gameViewHovered || runtime.Window.CursorMode == CursorMode.Locked;

        editorInput.NewFrame();
        var allowCameraInput = sceneViewHovered && !imgui.WantTextInput && !AsyncAssetImport.IsBusy;
        editorCamera.Update((float)delta, allowCameraInput, editorInput);
        MaybeSaveCameraPose((float)delta);

        // Hierarchy/menu "Create" drops new entities here — a short distance ahead of the scene camera
        // (Unity's create-in-front-of-SceneView), not at world origin. Refreshed every frame so it
        // tracks the current view.
        Transform camT = editorCamera.Transform;
        editorState.SceneSpawnPoint = camT.Position + camT.Forward * 10f;

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
        inputRouter.Dispatch(EditorInputContext.Global);
    }

    // A4: build the declarative hotkey table ONCE. Every shell hotkey that used to be an inline guard in
    // OnUpdate/BuildUI is a binding here, tagged with the context it belongs to (Global fires from any panel;
    // SceneView fires only while the Scene viewport is the active surface). The router resolves a chord to at
    // most one action per dispatch, by priority then Id -- so e.g. the bare-R gizmo binding (SceneView) and
    // Ctrl+R rebuild (Global) never collide: different chord, different context, and the conflict check
    // (asserted in the harness) verifies no two bindings share chord+context+priority. Bodies stay co-located
    // here with the methods/state they touch (same minimal-diff approach A2 used for the frame passes).
    EditorInputRouter BuildInputRouter() {
        var r = new EditorInputRouter(editorInput);

        // Global Ctrl chords -- fire regardless of focused panel (suppression while typing is the context gate,
        // applied by the caller). Same effect as the old HandleGlobalShortcuts block, in the same set.
        r.Bind(EditorActions.Undo, new KeyChord<Keys>(Keys.Z, ctrl: true), EditorInputContext.Global,
               () => { EditorUndo.Undo(); MarkSceneDirty(); });
        r.Bind(EditorActions.Redo, new KeyChord<Keys>(Keys.Y, ctrl: true), EditorInputContext.Global,
               () => { EditorUndo.Redo(); MarkSceneDirty(); });
        r.Bind(EditorActions.Save, new KeyChord<Keys>(Keys.S, ctrl: true), EditorInputContext.Global,
               SaveScene);
        r.Bind(EditorActions.RebuildScripts, new KeyChord<Keys>(Keys.R, ctrl: true), EditorInputContext.Global,
               RebuildScripts);

        // Scene-view gizmo mode (bare W/E/R, no modifiers -- the exact-modifier chord means Ctrl+R never selects
        // gizmo-scale, replacing the old `!KeyCtrl` guard). SceneViewHovered = the mouse must be over the Scene
        // image, matching the old `sceneViewHovered` guard (stricter than the focus/clipboard keys below).
        r.Bind(EditorActions.GizmoTranslate, new KeyChord<Keys>(Keys.W), EditorInputContext.SceneViewHovered,
               () => gizmo.Mode = GizmoMode.Translate);
        r.Bind(EditorActions.GizmoRotate, new KeyChord<Keys>(Keys.E), EditorInputContext.SceneViewHovered,
               () => gizmo.Mode = GizmoMode.Rotate);
        r.Bind(EditorActions.GizmoScale, new KeyChord<Keys>(Keys.R), EditorInputContext.SceneViewHovered,
               () => gizmo.Mode = GizmoMode.Scale);

        // Scene-view focus/clipboard (Unity mapping). F frames the selection; Ctrl+Shift+F aligns it to the
        // view; Ctrl+C / Ctrl+V copy / paste the selected entity. The exact-modifier match keeps Ctrl+Shift+F
        // from also firing plain Frame (the old fall-through bug), and Ctrl+C/V from triggering the gizmo keys.
        r.Bind(EditorActions.FrameSelected, new KeyChord<Keys>(Keys.F), EditorInputContext.SceneView,
               FocusSelected);
        r.Bind(EditorActions.AlignToView, new KeyChord<Keys>(Keys.F, ctrl: true, shift: true),
               EditorInputContext.SceneView, AlignSelectedToView);
        r.Bind(EditorActions.CopyEntity, new KeyChord<Keys>(Keys.C, ctrl: true), EditorInputContext.SceneView,
               CopySelected);
        r.Bind(EditorActions.PasteEntity, new KeyChord<Keys>(Keys.V, ctrl: true), EditorInputContext.SceneView,
               PasteClipboard);

        r.Build();
        // No per-action enabled-gate: SaveScene already handles the play-mode case itself (it logs the "can't
        // save while playing" warning and no-ops), so gating the dispatch here would SWALLOW that feedback --
        // behaviour-identical only if the action always runs, as before. The ActionEnabled hook exists for a
        // future binding that genuinely needs silent suppression.
        return r;
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

    // The editor frame loop, A2-decomposed into a declared ordered pass list (EditorFrameGraph). Built once
    // (lazily, on first frame) so the pass objects + the frozen order are allocated only once, never per
    // frame (perf constraint: zero per-frame alloc in the loop). The pass BODIES are the FramePass* methods
    // below — verbatim slices of the old single OnRender, in the EXACT same order the events encode.
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
        frameContext.RenderScene = false;   // ResolveDirty sets it; ViewportRender + IdleThrottle read it
        frameGraph.Execute(frameContext);
    }

    // ---- Editor frame passes (verbatim OnRender slices, ordered by EditorFramePassEvent) -------------

    // Run any main-thread completion work from a finished background import (thumbnail/asset
    // cache invalidation) before building this frame's UI off the fresh asset database.
    void FramePassImportPump(EditorFrameContext ctx) => AsyncAssetImport.PumpCompletion();

    // Remote commands (agents/MCP) execute here — on the main thread, before the UI builds,
    // so a remote edit and a human edit are indistinguishable to the rest of the frame.
    void FramePassRemotePump(EditorFrameContext ctx) => RemoteCommandQueue.Pump();

    // Build the UI FIRST (the gizmo mutates transforms there), then render the scene with
    // this frame's values â€” otherwise the object trails the gizmo by one frame.
    void FramePassBuildUI(EditorFrameContext ctx) {
        using (Profiler.Zone("Editor.BuildUI")) {
            imgui.Update((float)ctx.Delta);
            BuildUI();
            BusyOverlay.Draw(S);
            BusyOverlay.DrawBakeBadge(S); // non-blocking GI-bake indicator (the bake no longer modal-blocks)
        }
    }

    // Kick the startup asset import on the first painted frame (not in the constructor), so the
    // window and the busy overlay are already on screen instead of a black, frozen window. The
    // startup scene loads on the render thread once the import finishes.
    void FramePassStartupImport(EditorFrameContext ctx) {
        if (!startupImportKicked) {
            startupImportKicked = true;
            AsyncAssetImport.Request("Importing project assets...", onFinished: LoadStartupScene);
        }
    }

    // Decide whether to re-render the scene this frame; result flows to ViewportRender + IdleThrottle via
    // ctx.RenderScene. "Always refresh" off: re-render only while something is changing (playing, flying,
    // gizmo drag, recent interaction). The last image stays on screen.
    void FramePassResolveDirty(EditorFrameContext ctx) {
        // Skip the scene render while a deferred open is pending â€” the scene is about to be replaced.
        // A probe bake counts as "changing": its time-sliced job only advances inside the scene
        // render, so without this it crawls one slice per click instead of one per frame.
        var probeBakePending = ProbeRenderState.IsBaking;
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
        // Just-finished scene load: paint a burst of frames so AUTO-EXPOSURE can converge. Its meter
        // is an async GPU readback (several frames latency) that snaps on the first target — without a
        // burst the on-demand renderer would stop after a few frames and the scene stays at the stale
        // EV (pitch black for a dim/interior/imported scene). Falling edge of IsLoading.
        if (wasLoadingScene && !SceneCommands.IsLoading)
            forceFrames = Math.Max(forceFrames, 45);
        wasLoadingScene = SceneCommands.IsLoading;

        // A live game UIDocument animates per frame (tweens, pulses, loading), so the Game view must
        // keep repainting while one is active — otherwise on-demand rendering freezes the UI after the
        // initial forceFrames run out (it builds in the controller's OnAttach but never draws again).
        bool activeGameUI = !sceneTabActive && BallisticEngine.UI.UIDocument.Active.Count > 0;
        ctx.RenderScene = !SceneCommands.IsLoading &&
                          (alwaysRefresh || SceneManager.IsPlaying || editorInput.RightMouseDown ||
                           gizmo.IsInteracting || forceFrames > 0 || probeBakePending || activeGameUI);
    }

    // Render the active Scene/Game view offscreen with this frame's (post-BuildUI) values.
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

        // After the scene has rendered: resume `await Coroutine.EndOfFrame()` continuations (only
        // does anything while playing; the runner is empty otherwise).
        if (SceneManager.IsPlaying)
            Coroutine.EndOfFramePump();
    }

    // The DX12 host already cleared the swapchain backbuffer in Dx12BallisticEngineWindow.OnRenderFrame
    // (before this callback), so there's nothing to clear here.
    void FramePassImGuiRender(EditorFrameContext ctx) {
        using (Profiler.Zone("Editor.ImGuiRender"))
            imgui.Render();
    }

    // Pump the deferred scene open + record this frame's CPU cost. NOTE: the buffer swap happens after
    // OnRender returns, so a blocking apply here stalls BEFORE this frame presents â€” SceneCommands defers
    // the apply two frames after prefetch so its final status is actually on screen. Refresh thumbnails after.
    void FramePassPostPresent(EditorFrameContext ctx) {
        if (SceneCommands.PumpPendingOpen()) {
            assets.InvalidateThumbnails();
            pendingFocusWindow = EditorLayout.SceneView;
            MarkSceneDirty();
        }

        // Exponential moving average so the value is readable.
        editorCpuMs = editorCpuMs * 0.9f + (float)frameWatch.Elapsed.TotalMilliseconds * 0.1f;
    }

    // IDLE THROTTLE: when nothing is happening — not playing, no scene render, no mouse/keyboard
    // activity, no open popup — there's no point spinning ImGui at hundreds of FPS (wasted CPU/GPU/
    // battery for an identical frame). Drop to a low idle cap; snap back to full the instant the
    // user does anything. Skipped when the user picked an explicit FPS cap below the idle rate.
    void FramePassIdleThrottle(EditorFrameContext ctx) => UpdateIdleThrottle(ctx.RenderScene, ctx.Delta);

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
            // The DX12 host has no GL context (window.VSync would NRE); it presents vsync'd and the
            // UpdateFrequency cap paces it, so there's no VSync mode to toggle here.
        }
    }

    void MarkSceneDirty() => forceFrames = 3;

    // Applies the frame-rate limit from EditorPrefs. 0 = Adaptive VSync (lowest latency while we
    // keep up). A positive value disables VSync and caps the render/update loop to that FPS.
    public void ApplyFrameRateLimit() {
        int limit = EditorPrefs.Current.FrameRateLimit;
        if (limit <= 0) {
            window.UpdateFrequency = 0;   // uncapped; the DX12 swapchain presents vsync'd, which paces it
        }
        else {
            window.UpdateFrequency = limit;
        }
    }

    void RenderSceneView() {
        editorCamera.SetAspect((float)Math.Max(1, (int)sceneViewSize.X) / Math.Max(1, (int)sceneViewSize.Y));
        // Gate the gizmo depth-occlusion grid the ViewportRenderer publishes (gizmos drawn later this
        // frame dim when behind geometry). Editor policy lives here; the read happens inside the render.
        GizmoDepthOcclusion.Enabled = EditorPrefs.Current.ShowGizmos;
        viewport.RenderSceneView(editorCamera, sceneViewSize);
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

        gameCameraView.Bind(camera, (float)Math.Max(1, (int)gameViewSize.X) / Math.Max(1, (int)gameViewSize.Y));
        viewport.RenderGameView(gameCameraView, gameViewSize);
    }

    // ---- Layout -------------------------------------------------------------

    bool layoutInitialized;
    bool resetLayoutRequested;

    void BuildUI() {
        ImGuiIOPtr io = ImGui.GetIO();

        // Esc to exit maximize stays an ImGui key (NOT routed through the A4 raw-OpenTK router): ImGui's Escape
        // is modal/popup-aware (a popup eats it first), which is the wanted behaviour here, whereas the router's
        // raw probe would ignore that capture. The router owns the raw keyboard-shortcut surface; this one
        // UI-modal key intentionally stays with ImGui.
        if (maximize.IsMaximized && ImGui.IsKeyPressed(ImGuiKey.Escape))
            maximize.Clear();

        // Drop a stale fullscreen target: if the maximized panel was closed (its Window-menu toggle
        // turned off, or its duplicated instance closed), don't keep drawing it fullscreen forever —
        // fall back to the normal docked layout this frame. One can't-forget call: there is no state
        // path that stays maximized on a panel that's no longer drawable (the old "stuck maximized" bug).
        maximize.DropIfUnavailable(MaximizedPanelStillAvailable);

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
            // Keep the tab-strip band clamp valid while maximized too (this block returns before the
            // normal-path assignment runs) — else the clamp used a stale value and a maximized panel's
            // title double-click to restore stopped working.
            contentAreaTop = maxPos.Y;
            if (maximizedViewport)
                DrawMaximizedViewport(maxPos, maxSize);
            else
                DrawMaximizedPanel(maximizedPanel, maxPos, maxSize);

            // Exit-fullscreen button just under the toolbar (so it's not Esc-only). A small floating
            // overlay window above everything; clicking restores the docked layout.
            DrawExitFullscreenButton(workPos, workSize, toolbarH);

            // Floating tool windows stay available while a panel is fullscreen — keep this list in sync
            // with the normal-path block below (both must draw EVERY floating window or it vanishes in
            // one mode). tagsLayers was previously missing here, so Tags & Layers disappeared in fullscreen.
            settings.Draw(S);
            tagsLayers.Draw(S);
            layerCollision.Draw(S);
            profilerPanel.Draw(profiler, S);
            buildPanel.Draw(S);
            CurveEditorWindow.Draw(S);
            ComponentEditorWindow.Draw(S);
            UnityImportWindow.Draw(S);
            RenderPassTogglesWindow.Draw(S);
            DrawUnsavedPrompt();
            return;
        }

        // Fixed toolbar strip pinned under the menu bar (not dockable).
        Panel("##toolbar", workPos, new SysVec2(workSize.X, toolbarH),
            PanelFlags | ImGuiWindowFlags.NoTitleBar, ToolbarUI);

        // Full-window host window owning the central DockSpace. Transparent + chromeless so the docked
        // panels read as the whole editor; sits below the toolbar strip.
        SysVec2 hostPos = workPos + new SysVec2(0, toolbarH);
        contentAreaTop = hostPos.Y;   // the tab-strip band must not reach above this (into the toolbar)
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
        // EF9c: NO PassthruCentralNode. It only matters when the central node is EMPTY (it makes the empty
        // node transparent + click-through to whatever is behind the host window). Here the central node is
        // ALWAYS filled by the Scene/Game view windows, so passthrough never engaged visibly — but the flag
        // still suppresses the central node's own background/hit-target, which the review flagged as a
        // maximize/modal-capture breaker (a fullscreen/modal over an empty-feeling central node could leak
        // input through). Dropping it is byte-identical to the eye (central node always has a window) and
        // removes the hazard. The host window already has NoBackground, so nothing relied on passthrough.
        ImGui.DockSpace(dockId, SysVec2.Zero, ImGuiDockNodeFlags.None);

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

        // Dockable core panels — normal windows ImGui places into the dock tree. A1b-deeper: walk the
        // registry instead of five named DrawDockPanel calls; the registry owns each panel's Shown state
        // and writes the close-button result back through DrawDockPanel's `ref show`. EditorApplication
        // names no core panel here. The declaration order (Entities, Scene-components, Inspector, Assets,
        // Console) reproduces the old draw order.
        // IMPORTANT: once Begin() is called it MUST be paired with End(), even if Begin returns false
        // (collapsed) OR the close button set show=false this frame. The old "if (show) End()" dropped
        // the End() when the X was clicked (Begin already drew the content + opened a BeginChild that
        // frame), leaving "Missing EndChild()" and corrupting all ImGui state. DrawDockPanel handles it.
        panels.DrawCore(DrawDockPanel);

        // Extra (duplicated) panel instances opened from the Add Tab menu. (Drawn after the core panels
        // now that the latter are one registry loop; these are floating-centered windows ImGui places by
        // ###id, so the Begin order relative to Assets/Console is immaterial.)
        extraPanels.DrawAll();

        // Scene + Game are separate dockable windows (were inner viewport tabs).
        DrawViewportWindows();

        settings.Draw(S);
        tagsLayers.Draw(S);
        profilerPanel.Draw(profiler, S);
        buildPanel.Draw(S);
        CurveEditorWindow.Draw(S);
        ComponentEditorWindow.Draw(S);   // standalone component window — was only drawn while fullscreen
        UnityImportWindow.Draw(S);
        RenderPassTogglesWindow.Draw(S);
        DrawUnsavedPrompt();

        // Persist the layout whenever ImGui says it changed (drag/dock/resize/tab).
        if (io.WantSaveIniSettings) {
            EditorLayout.Save();
            io.WantSaveIniSettings = false;
        }

        // EF9c: persist the open/closed panel set separately from the .ini — closing a panel (the X) only
        // flips our Shown flag, which doesn't dirty ImGui's dock settings, so WantSaveIniSettings above can
        // stay false. Save only when the set actually changed (cheap string compare, no per-frame file I/O).
        string hidden = string.Join('\n', panels.HiddenKeys());
        if (hidden != lastSavedPanelState) {
            EditorLayout.SavePanelState(panels.HiddenKeys());
            lastSavedPanelState = hidden;
        }
    }

    // The last panel-visibility set persisted to disk (EF9c) — guards against re-writing the sidecar every
    // frame. null until the first save so the initial state is always written once.
    string lastSavedPanelState;

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
            // Discovered [MenuItem("Assets/...")] commands (the Unity-package importer self-registers here).
            DrawRegistryMenu("Assets");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window")) {
            // The window list is BUILT from the self-registered [MenuItem("Window/...")] commands
            // (EditorWindowRegistry), not hand-listed — Rule 3: EditorApplication names no window here.
            // The core panels (Order 0-4) and the standalone tools (Order 20+) render with a checkmark
            // mirroring their open state; the registry separates the two groups by Order with a divider.
            DrawRegistryMenu("Window");

            // Layout operations (not windows, so not registry-discovered): open another panel instance,
            // and reset the dock arrangement.
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

    // Renders every self-registered [MenuItem("<topMenu>/...")] command under the open menu (Rule 3 / A1).
    // Nested paths become sub-menus; a 2-segment path ("Window/Inspector") is a leaf directly under the
    // top menu. A leaf whose path maps to a window key shows a Unity-style checkmark mirroring that
    // window's open state (and is disabled when EditorWindows.IsEnabled is false). Entries come pre-sorted
    // deterministically from the registry; a big jump in Order between consecutive leaves inserts a divider
    // (so the core panels and the standalone tools stay visually grouped, as the old hand-list did).
    void DrawRegistryMenu(string topMenu) {
        int? prevOrder = null;
        foreach (EditorWindowRegistry.Entry entry in EditorWindowRegistry.Items) {
            if (entry.TopMenu != topMenu) continue;

            // Group divider: a >10 Order gap from the previous leaf in this menu separates groups.
            if (prevOrder is { } po && entry.Order - po > 10)
                ImGui.Separator();
            prevOrder = entry.Order;

            // Open the sub-menus this entry nests under, in order, then close them after the leaf.
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
    }

    void DrawGameObjectMenu() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.MenuItem("Create Empty")) {
            // F1: structural create routed through the EditorCommands choke point (byte-identical to the
            // old "Push(); mutate();" -- the snapshot scope is now chosen by EditorCommands, not here).
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
                // Only proceed if the save actually succeeded — otherwise (e.g. blocked in play mode, or
                // no scene path) we'd silently discard the unsaved changes the prompt is protecting.
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

        // RW3 E7: the gizmo Move/Rotate/Scale + Pivot/Center group moved OUT of this cramped top app bar
        // into the in-viewport overlay (DrawSceneViewToolbar). The top bar now carries only the app-level
        // controls (scene name, undo/redo, transport, save) — scene-manipulation tools live on the 3D view.
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
            if (ImGui.Button(EditorIcons.Stop, new SysVec2(buttonW, 0)))
                playMode.ExitPlay();   // A5: StopPlay -> cursor reset + selection clear + focus Scene view
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stop (exit play mode)");
        }
        else {
            // Unity's compile-error lock: while the latest script compile failed, the Play
            // button is disabled with the reason in its tooltip (StartPlay also self-guards).
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
                playMode.EnterPlay();   // A5: save-guard -> StartPlay -> focus Game view
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
        // Save is disabled in play mode — the live scene is play-mutated and would clobber the edit
        // scene on disk (SceneCommands.Save also guards this).
        ImGui.BeginDisabled(SceneManager.IsPlaying);
        if (EditorIcons.GhostButton("save", EditorIcons.Save,
                SceneManager.IsPlaying ? "Stop play to save" : "Save scene (Ctrl+S)"))
            SaveScene();
        ImGui.EndDisabled();
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

    // (RW3 E7: the Move/Rotate/Scale + Pivot/Center group moved to the in-viewport overlay —
    // DrawSceneViewToolbar. GizmoModeButton (below) is still the segmented-control button, now called
    // from the overlay instead of a top-bar GizmoModeToolbar.)

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

        // Custom resolution: two editable int fields shown only when "Custom..." is selected.
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
            // Custom aspect: two small int fields for the ratio (e.g. 21 : 9). Fills the panel,
            // letterboxed to this ratio, no fixed pixel count.
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

        // Right side, laid out right-to-left: render resolution, FPS-limit button, and (Scene
        // view only) the grid / gizmos / space / snap controls as compact icon toggles.
        float pad2 = ImGui.GetStyle().FramePadding.X * 2;
        float right = ImGui.GetWindowWidth() - 14 * S;
        void RightAlign(float w) { right -= w; ImGui.SameLine(right); right -= 6 * S; }

        // FIXED-WIDTH reservations for the resolution text and the FPS button: their digit counts
        // change every frame (1920x1080 vs 800x600, 500 fps vs 60 fps), so measuring the live string
        // made every control to their LEFT jump around. Reserve the widest case once.
        SysVec2 rs = id == "scene" ? sceneViewSize : gameViewSize;
        var resText = $"{(int)rs.X} x {(int)rs.Y}";
        float resW = ImGui.CalcTextSize("8888 x 8888").X;
        RightAlign(resW);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(resText);

        var fpsLabel = $"{runtime.Window.FrameRate:0} fps {EditorIcons.ChevronDown}";
        float fpsW = ImGui.CalcTextSize($"8888 fps {EditorIcons.ChevronDown}").X + pad2;
        RightAlign(fpsW);
        DrawFpsButton(fpsLabel);

        // Stats overlay toggle, Unity's Game-view "Stats" button style (the overlay's X also closes it).
        var statsLabel = $"{EditorIcons.Info} Stats";
        RightAlign(ImGui.CalcTextSize(statsLabel).X + pad2);
        ToggleIconButton($"statsbar{id}", statsLabel, ref showStats, "Statistics overlay");

        // (The maximize BUTTON was removed — double-click any panel's tab to fullscreen it, Esc to
        // restore. Works for every panel now, so a dedicated viewport button is redundant.)

        // Shading-mode dropdown: the engine's Shaded / Wireframe / Normals / Depth PLUS the editor-only
        // extra views (AO / Lit / Luminance) that live in EditorDebugViews (never in a player build).
        // RW3 E8: SCENE VIEW ONLY now — the Game view is the player's-eye output, so the editor-only debug
        // views (AO/Lit/Luminance + GI-isolate) belong on the Scene view, not the Game view (where they'd
        // misrepresent what the shipped game shows). Per-view popup id kept for safety.
        if (id == "scene") {
            var engineNames = new[] { "Shaded", "Wireframe", "Normals", "Depth" };
            int extra = HDRenderer.EditorExtraDebugMode;
            string current = extra != 0
                ? Array.Find(EditorDebugViews.Modes, m => m.mode == extra).label
                : engineNames[(int)Renderer.DebugViewMode];
            var modeLabel = $"{current} {EditorIcons.ChevronDown}";
            RightAlign(ImGui.CalcTextSize($"Ambient Occlusion {EditorIcons.ChevronDown}").X + pad2);
            if (EditorIcons.GhostButton($"shadingmode{id}", modeLabel, "Shading / debug view mode"))
                ImGui.OpenPopup($"##shadingmode{id}");
            if (ImGui.BeginPopup($"##shadingmode{id}")) {
                ImGui.TextDisabled("Shading Mode");
                ImGui.Separator();
                for (var i = 0; i < engineNames.Length; i++) {
                    bool sel = extra == 0 && (int)Renderer.DebugViewMode == i;
                    if (ImGui.MenuItem(engineNames[i], (string)null, sel)) {
                        Renderer.DebugViewMode = (HDRenderer.DebugView)i;
                        HDRenderer.EditorExtraDebugMode = EditorDebugViews.None;   // leave any extra view
                        editorState.MarkViewportDirty();
                    }
                }
                ImGui.Separator();
                ImGui.TextDisabled("Buffers (editor only)");
                foreach (var (mode, label) in EditorDebugViews.Modes) {
                    if (ImGui.MenuItem(label, (string)null, extra == mode)) {
                        HDRenderer.EditorExtraDebugMode = mode;
                        Renderer.DebugViewMode = HDRenderer.DebugView.Shaded; // engine renders normally; we replace composite
                        editorState.MarkViewportDirty();
                    }
                }

                // GI ISOLATE menu REMOVED (2026-06-17): the Lumen diffuse-GI stack is hard-disabled
                // engine-wide, so isolating its contribution is meaningless. Force the (DX12-dead) isolate
                // state back to None so nothing stale lingers.
                HDRenderer.EditorGiIsolate = HDRenderer.GiIsolate.None;
                ImGui.EndPopup();
            }
        }

        // RW3 E7: the Scene-view-only controls (snap chip, World/Local space, component-gizmos, grid, and the
        // light/reflection probe GI-debug toggles) moved OUT of this thin resolution bar into the in-viewport
        // overlay (DrawSceneViewToolbar). They drove the same gizmo.Space / EditorPrefs / ProbeRenderState
        // state — only their location changed. The resolution bar now carries only res / zoom / fps / stats /
        // shading-mode, shared cleanly between the Scene and Game views.

        ImGui.Separator();
    }

    // Double-clicking a viewport window's tab/title strip toggles fullscreen for that view. Call right
    // after Begin. For a DOCKED window the tab bar sits ABOVE the content origin (GetWindowPos().Y), so
    // the hit band extends upward by ~2 frame heights to cover the dock tab; for a floating window it
    // covers the title bar. The horizontal span is the window width. Excludes the content area so a
    // double-click on the 3D image (gizmo/selection) never maximizes.
    // A small "exit fullscreen" button floated at the top-right while a panel is maximized, so leaving
    // fullscreen isn't Esc-only (the user couldn't find a way out). Clicking it clears maximizedPanel.
    void DrawExitFullscreenButton(SysVec2 workPos, SysVec2 workSize, float toolbarH) {
        float margin = 8 * S;
        // Centered at the TOP of the view (pivot 0.5,0), just under the toolbar — where the user
        // expects it, out of the way of the left-side controls.
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

    // ── EditorWindows facade handlers (A1 / Rule 3) ─────────────────────────────────────────────────
    // The static EditorWindows facade (called by the self-registered [MenuItem] window commands) routes
    // here so the menu's open/toggle acts on this live instance. Keys are the EditorLayout.* dock names for
    // the core panels and EditorMenus.WindowKeys.* for the standalone tool windows. These three handlers
    // REPLACE the per-window `ref showXxx` / `ref panel.Open` arguments that DrawMainMenuBar used to pass by
    // name — the menu no longer references a window field directly; it toggles through the key.

    // Toggle a window's visibility (the Window-menu checkbox behaviour). Closed → (re)open + focus it.
    // A1b-deeper: the five core panels collapse to one registry call (it owns their Shown state); only the
    // standalone tool windows keep their own field-backed open flags.
    void ToggleWindow(string key) {
        if (panels.IsCorePanel(key)) {
            if (panels.Toggle(key)) pendingFocusWindow = key;   // focus when it just (re)opened
            return;
        }
        switch (key) {
            case EditorMenus.WindowKeys.Statistics: showStats = !showStats; break;
            case EditorMenus.WindowKeys.Profiler: profilerPanel.Open = !profilerPanel.Open; break;
            case EditorMenus.WindowKeys.Build: buildPanel.Open = !buildPanel.Open; break;
            case EditorMenus.WindowKeys.TagsLayers: tagsLayers.Open = !tagsLayers.Open; break;
            case EditorMenus.WindowKeys.LayerCollision: layerCollision.Open = !layerCollision.Open; break;
            case EditorMenus.WindowKeys.Settings: settings.Open = !settings.Open; break;
        }
    }

    // Open a window (never closes). For a core panel whose primary is hidden, bring the primary back;
    // otherwise spawn ANOTHER instance through the host (the "Add Panel" behaviour). For a standalone
    // window, just open it.
    void OpenWindow(string key) {
        if (panels.IsCorePanel(key)) {
            if (!panels.Show(key))          // re-show a hidden primary; if already shown,
                extraPanels.Open(key);      // add another instance via the host
            return;
        }
        switch (key) {
            case EditorMenus.WindowKeys.Statistics: showStats = true; break;
            case EditorMenus.WindowKeys.Profiler: profilerPanel.Open = true; break;
            case EditorMenus.WindowKeys.Build: buildPanel.Open = true; break;
            case EditorMenus.WindowKeys.TagsLayers: tagsLayers.Open = true; break;
            case EditorMenus.WindowKeys.LayerCollision: layerCollision.Open = true; break;
            case EditorMenus.WindowKeys.Settings: settings.Open = true; break;
            case EditorMenus.WindowKeys.UnityImport: UnityImportWindow.Open(); break;
        }
    }

    // Whether a window is currently shown (drives the Window-menu checkmark via EditorWindows.IsOpen).
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
            _ => false,
        };
    }

    // Per-window menu-enable gate (reserved for future "disabled while playing" cases; all enabled today).
    bool IsWindowEnabled(string key) => true;

    // Draws one dockable panel with a CORRECT Begin/End pairing: End() is always called once Begin()
    // ran, even when Begin returns false or the close button just set `show` to false this frame. The
    // content (+ the maximize/add-tab strip handler) only runs when Begin returned true.
    void DrawDockPanel(string name, ref bool show, Action drawContents) {
        // EF9d: a core panel re-opened from the Window menu (ToggleWindow flipped Shown false->true and
        // set pendingFocusWindow = key) must SURFACE — otherwise it re-appears behind whatever tab shares
        // its dock node and the toggle reads as a no-op. DrawCore runs before DrawViewportWindows (which
        // clears pendingFocusWindow), so the flag is still live here; the viewports consume their own keys
        // (SceneView/GameView) and never match a core-panel key, so there is no conflict. Same Unity-style
        // focus-on-open the viewports already get, now extended to the core dockable panels.
        if (pendingFocusWindow == name) ImGui.SetNextWindowFocus();
        // EF12: the docked tab/title is the descriptor's DISPLAY Title, with the panel KEY as the ImGui
        // `###id`. ImHashStr resets at the last `###`, so the window id is still hash(name) — the dock-.ini
        // `[Window][<key>]` entry, the dock-builder's DockBuilderDockWindow(key) target, and the `.panels`
        // sidecar all match unchanged. This makes the docked title source agree with the maximized
        // (DrawMaximizedPanel) and multi-instance (DockPanelHost) paths, which already show d.Title — and
        // is what surfaces "Inspector"→"Details" (and "Scene"→"Scene Components") on the docked tab.
        EditorPanelRegistry.Descriptor dd = panels.Get(name);
        string label = dd is not null ? $"{dd.Title}###{name}" : name;
        bool visible = ImGui.Begin(label, ref show);
        if (visible) {
            MaximizePanelOnTitleDoubleClick(name);
            drawContents();
        }
        ImGui.End();
    }

    // Double-clicking a window's tab/title strip toggles fullscreen for THAT panel (works for every
    // dockable panel now, not just the viewports). Call right after the panel's Begin. For a DOCKED
    // window the tab bar sits ABOVE the content origin, so the hit band extends upward by ~1.4 frame
    // heights; for a floating window it covers the title bar. Excludes the content area.
    void MaximizePanelOnTitleDoubleClick(string panelName) {
        // Hit-test the title/tab strip purely geometrically — do NOT gate on IsWindowHovered, because a
        // docked window's tab strip is owned by the dock-node parent, so this window is NOT "hovered"
        // while the cursor is on its own tab (the old bug: the interaction was silently dropped).
        // GetCursorStartPos is the content origin in window-local coords (just below the title/tab strip);
        // window pos + that Y is where content rows begin. The strip is [windowTop .. contentTop].
        SysVec2 mouse = ImGui.GetIO().MousePos;
        SysVec2 winPos = ImGui.GetWindowPos();
        float winW = ImGui.GetWindowSize().X;
        float contentTop = winPos.Y + ImGui.GetCursorStartPos().Y;
        float stripTop = winPos.Y - (ImGui.IsWindowDocked() ? ImGui.GetFrameHeight() : 0f);
        // CLAMP the band so it can't reach up over the toolbar — a docked viewport sits right under it,
        // and the band's upward extension used to overlap the toolbar's Save/undo buttons, so clicking
        // those fullscreened the view. Keeping the band below the toolbar fixes that WITHOUT killing the
        // tab double-click (the old IsAnyItemHovered guard also blocked the tab itself).
        stripTop = Math.Max(stripTop, contentAreaTop);
        bool onStrip = mouse.Y >= stripTop && mouse.Y < contentTop &&
                       mouse.X >= winPos.X && mouse.X <= winPos.X + winW;

        // Double-click the strip → toggle fullscreen for this panel.
        if (onStrip && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            maximize.Toggle(panelName);

        // Right-click the strip → "Add Tab" menu to open any closed panel (Unity/VS dock behaviour).
        if (onStrip && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"##tabctx_{panelName}");
        if (ImGui.BeginPopup($"##tabctx_{panelName}")) {
            // Add Tab → click a kind to open ANOTHER instance of it (unlimited). Each entry shows how
            // many are open. Singleton views (Scene/Game) aren't here — they're one-per-renderer-target.
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

    // One "Add Tab" entry: opens ANOTHER instance of the kind. If the primary (id-0) panel is closed,
    // re-show it first; otherwise spawn an extra instance through the host. The count is shown as a hint.
    // A1b-deeper: the primary show-state is the registry's, not a ref bool — Show() re-opens a hidden
    // primary (returns true), else we add an extra host instance, exactly as before.
    void AddTabItem(string kindKey, string label) {
        int total = (panels.IsShown(kindKey) ? 1 : 0) + extraPanels.CountOf(kindKey);
        string hint = total > 0 ? $"{total} open" : null;
        if (ImGui.MenuItem(label, hint)) {
            if (!panels.Show(kindKey))                  // bring the main one back first; if already shown,
                extraPanels.Open(kindKey);              // add another
        }
    }

    // Draws one panel filling the whole work area while maximized (anything except the viewports,
    // which take DrawMaximizedViewport). Routes by the panel's layout name to its contents.
    // Whether the currently-maximized panel is still a thing we can draw fullscreen. Viewports always
    // are; a primary dock panel is only available while its Window-menu toggle is on; a duplicated
    // (host) instance is available while the host still owns its label. Returns false once the panel
    // has been closed, so BuildUI can drop the stale fullscreen target.
    bool MaximizedPanelStillAvailable(string name) {
        // A1b: single-sourced. A core panel (or viewport) is "available" per its ONE registry descriptor
        // (viewports always; a normal panel while its show-toggle is on). A duplicated (Add Tab) instance
        // is available while the host still owns its label. No more per-panel switch to keep in sync.
        if (panels.Contains(name)) return panels.IsAvailable(name);
        return extraPanels.OwnsLabel(name);
    }

    void DrawMaximizedPanel(string name, SysVec2 pos, SysVec2 size) {
        // A duplicated (Add Tab) panel's label is owned by the host, not one of the primary layout
        // names below — route it to the host so double-clicking an extra tab can fullscreen it too
        // (previously these hit the "can't be shown fullscreen" dead-end). EF9a: the host now threads a
        // `ref open` so the X works while maximized; if it closed this frame, exit fullscreen NOW (the
        // instance is gone, so leaving it maximized would draw a stale target one frame / re-show it).
        if (extraPanels.OwnsLabel(name)) {
            if (extraPanels.DrawMaximizedInstance(name, pos, size, MaximizePanelOnTitleDoubleClick))
                maximize.Clear();
            return;
        }

        // A1b: single content path. The panel's body comes from its ONE registry descriptor — the same
        // DrawContents the normal docked path uses — so there is no parallel maximize-content chain to
        // drift, and no "can't be shown fullscreen" dead-end for a registered panel.
        //
        // EF9b (doesn't-fight-docking): the maximized window now has its OWN ImGui identity — a dedicated
        // `###maxpanel` id with NoSavedSettings — instead of reusing the docked panel's bare `name`
        // ("Inspector"/"Scene"/…) label. Sharing the docked identity made maximize FIGHT docking: ImGui
        // can't have one window be both docked (a member of the dock tree, geometry saved to the .ini)
        // and a NoDocking fixed-position fullscreen window at once, so each maximize force-undocked the
        // panel and (without NoSavedSettings) wrote its fullscreen pos/size into that window's saved
        // settings — polluting the layout EF9c will persist. The dedicated id leaves the docked window's
        // identity, dock-node membership, and saved geometry completely untouched (same approach the
        // viewport path already used via `##viewportmax`). The panel content is the SAME DrawContents
        // delegate, so the maximized view and docked view still share one instance/state.
        //
        // EF9a contract PRESERVED: thread a `ref open` (the panel's single-owned Shown flag) so the X is
        // drawn AND honored while maximized — NOT a `Begin(label, flags)` with no p_open. On close, flip
        // the registry Shown flag (so it STICKS next frame, no redraw loop) AND clear the maximize state
        // this same frame so the docked layout returns immediately. Restore-by-title-double-click keys off
        // `name` (the maximize KEY), not the window label, so it stays a no-op-toggle that restores.
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        EditorPanelRegistry.Descriptor d = panels.Get(name);
        string title = d is not null ? $"{d.Icon}  {d.Title}" : name;
        bool open = panels.IsShown(name);
        if (ImGui.Begin($"{title}###maxpanel", ref open, flags)) {
            MaximizePanelOnTitleDoubleClick(name); // double-click its title again to restore (keys off the maximize key)
            if (d?.DrawContents is not null)
                d.DrawContents();
            else {
                // Not a registered, body-drawable panel (shouldn't reach here — the stale-drop clears an
                // unknown target). Give a way out so it can never get stuck maximized.
                ImGui.TextDisabled("This panel can't be shown fullscreen.");
                if (ImGui.Button("Exit Fullscreen")) maximize.Clear();
            }
        }
        ImGui.End();
        if (!open) {                  // X clicked while maximized: honor the close everywhere
            panels.SetShown(name, false);
            maximize.Clear();
        }
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
            // Maximize THIS view explicitly — never via the shared gameViewFocused flag, which is reset
            // to false above and only set true inside GameTabContents (after this runs), so routing the
            // Game tab's double-click through it maximized the Scene view by mistake.
            MaximizePanelOnTitleDoubleClick(EditorLayout.SceneView);
            // The view whose window is focused drives offscreen render selection (OnRender).
            if (ImGui.IsWindowFocused()) sceneTabActive = true;
            SceneTabContents();
        }
        ImGui.End();

        if (pendingFocusWindow == EditorLayout.GameView) ImGui.SetNextWindowFocus();
        if (ImGui.Begin(EditorLayout.GameView)) {
            MaximizePanelOnTitleDoubleClick(EditorLayout.GameView);   // (un)fullscreen the Game view
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
            // Show whichever view was ACTUALLY maximized — drive off maximizedPanel, not sceneTabActive,
            // which could be out of sync (the "maximize Scene, exit, switch to Game, things get weird"
            // bug). Keep sceneTabActive in step so the on-demand render picks the right target.
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

        // Drag assets from the browser straight onto the 3D view (Unity parity): model/prefab → spawn,
        // script → entity-with-component, placed in front of the camera. Previously only the hierarchy
        // and inspector accepted asset drops, so dropping onto the viewport did nothing.
        if (ImGui.BeginDragDropTarget()) {
            if (hierarchy.DropAssetsIntoScene())
                MarkSceneDirty();
            ImGui.EndDragDropTarget();
        }
        // Hairline frame so the rendered image reads as a deliberate surface, not a raw blit.
        ImGui.GetWindowDrawList().AddRect(imageMin, imageMin + imageSize,
            ImGui.GetColorU32(new SysVec4(1, 1, 1, 0.06f)));
        sceneViewHovered = ImGui.IsItemHovered();
        gameViewFocused = false;
        gameViewHovered = false;

        // RW3 E7 — Unity-style IN-VIEWPORT toolbar: scene-manipulation tools (gizmo mode/pivot/space/snap)
        // float as an overlay on the top-left of the 3D image; the visibility menu (grid/gizmos/probes) sits
        // top-right. This declutters the cramped top app bar. Drawn AFTER the image so the overlay's child
        // windows sit ON TOP (clicks land on the buttons, and a button row is never "image-hovered" → bare
        // W/E/R don't fire under it). It does NOT change behaviour — same gizmo.Mode/Pivot/Space, same
        // EditorPrefs.ShowGrid/ShowGizmos, same ProbeRenderState toggles — only their LOCATION moved.
        DrawSceneViewToolbar(imageMin, imageSize);

        // A4: the scene-view hotkeys (gizmo mode W/E/R + Unity focus/clipboard F, Ctrl+Shift+F, Ctrl+C/V) now
        // route through the input router instead of two inline guard blocks. The two original blocks had DIFFERENT
        // guards, so they stay TWO dispatch calls, each reproducing its old gate EXACTLY (move != fit -- I keep
        // the precise guard split rather than merge into one stronger gate):
        //
        //   Gizmo W/E/R (SceneViewHovered): old guard `sceneViewHovered && !RightMouseDown && !KeyCtrl`. The
        //   `!KeyCtrl` is now the exact-modifier chord match (bare W/E/R never fire while Ctrl is held), so the
        //   live guard is just hover + not-flying. NOTE: the old gizmo block did NOT suppress on WantTextInput,
        //   so this one doesn't either (faithful). ONE intentional tightening: exact-modifier match also means
        //   bare W/E/R no longer fire while SHIFT is held (the old `!KeyCtrl`-only guard let Shift+W change the
        //   gizmo mode -- an undocumented accident, not intended). This is A4's defining contract ("a bare-key
        //   binding means exactly that key, no modifiers") and the only deliberate behaviour delta in the move.
        //
        //   Focus/clipboard F, Ctrl+Shift+F, Ctrl+C/V (SceneView): old guard `!RightMouseDown && !WantTextInput`
        //   -- worked from ANY panel while the Scene tab is showing (select in the Hierarchy, press F to fly the
        //   camera there). The exact-modifier match replaces the hand-written `if (ctrl && shift) align else if
        //   (!ctrl && !shift) frame` fall-through, so Ctrl+Shift+F no longer falls through to plain Frame.
        //
        // Both read modifiers + key edges from RAW OpenTK (EditorInput) -- the focus/clipboard keys already did;
        // routing the gizmo keys through the same probe makes that uniform (and is behaviour-equivalent under the
        // preserved hover/Ctrl gating).
        if (sceneViewHovered && !editorInput.RightMouseDown)
            inputRouter.Dispatch(EditorInputContext.SceneViewHovered);
        if (!editorInput.RightMouseDown && !imgui.WantTextInput)
            inputRouter.Dispatch(EditorInputContext.SceneView);

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
        else if (ProbeRenderState.AnyDebugActive) {
            // The probe-debug overlays are their OWN tool, independent of the component-gizmo toggle —
            // so they still draw when component gizmos are off. (When gizmos ARE on, DrawComponentGizmos
            // already draws them.) Needs its own gizmoDrawer.Begin since DrawComponentGizmos was skipped.
            gizmoDrawer.Begin(editorCamera, gizmoMin, gizmoSize, ImGui.GetWindowDrawList());
            ProbeRenderState.DrawProbes(gizmoDrawer);
            ProbeRenderState.DrawReflections(gizmoDrawer);
        }

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
                sceneViewHovered && !ColliderHandles.IsInteracting && !WheelHandles.IsInteracting && !TerrainTool.Armed);

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

        // DEBUG: probe-grid overlays (toolbar toggles / GI volume override). Always-on (not
        // selection-gated) so the implicit auto-fit volumes show too — light probes green=occupied /
        // red=air, reflection probes show occupied cubemap cells. The visual for the probe-density work.
        ProbeRenderState.DrawProbes(gizmoDrawer);
        ProbeRenderState.DrawReflections(gizmoDrawer);

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

            // WheelColliders get drag handles for radius + suspension travel (the wheel circle and
            // travel line they draw are their own OnDrawGizmosSelected). Same hover suppression.
            foreach (Behaviour behaviour in selected.Behaviours)
                if (behaviour is WheelCollider wheel &&
                    WheelHandles.Draw(wheel, editorCamera, imageMin, imageSize,
                        ImGui.GetWindowDrawList(), sceneViewHovered && !gizmo.IsInteracting && !ColliderHandles.IsInteracting))
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
            // (The IrradianceVolume box-resize handles were removed with the GL probe baker — P0.5. The
            // unified GlobalIllumination volume is a global post-process override with no in-world bounds.)
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

        // Single-entity transform edit -> scoped through EditorCommands.EditEntity (PushEntity: the
        // selection survives and no scene-wide IrradianceVolume re-bake fires). Byte-identical mutate.
        Transform cam = editorCamera.Transform;
        EditorCommands.EditEntity(selected, "Align To View", () => {
            selected.transform.Position = cam.Position;
            selected.transform.Rotation = cam.Rotation;
            MarkSceneDirty();
        });
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

        // Structural create (the pasted clone is a new entity). The snapshot fires at the same point as
        // before -- after the HasCopy guard, before Paste runs -- so undo is byte-identical.
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
        if (files is null || files.Count == 0) {
            Debugging.LogWarning("Drop import: the OS reported no files.");
            return;
        }

        var destFolder = Path.Combine(bootstrap.Project.RootPath,
            assets.CurrentFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destFolder);

        var copied = 0;
        foreach (var source in files) {
            // Dropping a FOLDER copies its whole tree in (Unity parity); a file copies itself.
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
                // Never silently skip a duplicate — land it under a unique name so a batch drop always
                // imports SOMETHING (the old code skipped every name collision, which read as "nothing
                // got added" when re-dropping the same files).
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

    // file.png -> file.png, file 1.png, file 2.png, ... (so a batch drop never collides itself away).
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

    // RW3 E7 — the in-viewport toolbar overlay. Two floating, auto-sized child windows pinned to the Scene
    // image's top-left (tools) and top-right (visibility), drawn over the 3D view. Flags: no decoration / no
    // docking / no nav / no saved-settings / no focus-on-appearing so the overlay never steals focus from the
    // viewport or persists into the .ini layout. The styling reads from EditorTheme.Overlay* so the chrome
    // matches the panels. Behaviour is the SAME state these controls always drove (gizmo / EditorPrefs /
    // ProbeRenderState) — RW3 only relocates them out of the cramped top bar.
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

        // ── LEFT: gizmo tools (Move/Rotate/Scale + Pivot/Center + World/Local + snap chip) ──────────────
        ImGui.SetNextWindowPos(new SysVec2(imageMin.X + margin, imageMin.Y + margin), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(EditorTheme.OverlayBg.W);
        if (ImGui.Begin("##sceneToolsOverlay", flags)) {
            // Move/Rotate/Scale segmented control on its own dark pill, mirroring the old top-bar group.
            // EF1: size each mode button to fit its icon+label (the fixed 58*S clipped "Move"/"Rotate"/
            // "Scale" to "Mov"/"Rot"/"Sca"). Measure all three labels, take the widest, add frame padding,
            // floor at 58*S so they stay a visually equal segmented group. Same for the Pivot/Center button.
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

            // Pivot / Center.
            ImGui.SameLine(0, 10 * S);
            bool isPivot = gizmo.Pivot == GizmoPivot.Pivot;
            float pivotW = MathF.Max(58 * S,
                MathF.Max(ImGui.CalcTextSize("Pivot").X, ImGui.CalcTextSize("Center").X) + framePadX);
            if (ImGui.Button(isPivot ? "Pivot" : "Center", new SysVec2(pivotW, h)))
                gizmo.Pivot = isPivot ? GizmoPivot.Center : GizmoPivot.Pivot;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isPivot ? "Handle at the entity's pivot (click for Center)"
                                         : "Handle at the selection's center (click for Pivot)");

            // World / Local gizmo space.
            ImGui.SameLine(0, 6 * S);
            bool world = gizmo.Space == GizmoSpace.World;
            string spaceIcon = world ? EditorIcons.World : EditorIcons.Package;
            if (EditorIcons.GhostButton("ovgizmospace", spaceIcon,
                    world ? "Gizmo space: World (click for Local)" : "Gizmo space: Local (click for World)"))
                gizmo.Space = world ? GizmoSpace.Local : GizmoSpace.World;

            // Snap indicator chip (lit while Ctrl is held — hold Ctrl to snap a drag).
            ImGui.SameLine(0, 8 * S);
            ImGui.AlignTextToFramePadding();
            bool snapOn = ImGui.GetIO().KeyCtrl;
            if (snapOn) ImGui.TextColored(EditorPrefs.Current.Accent, $"{EditorIcons.Grid} Snap");
            else { ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim); ImGui.Text($"{EditorIcons.Grid} Snap"); ImGui.PopStyleColor(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Ctrl while dragging a gizmo to snap.");
        }
        ImGui.End();

        // ── RIGHT: visibility menu (grid / gizmos / GI-debug probes) ─────────────────────────────────────
        // EF2: the orientation axis-ball (OrientationGizmo.Draw, called from DrawSceneView) also anchors
        // top-right of the viewport, so the eye-menu used to overlap its lower axis balls. Push the eye-menu
        // DOWN below the gizmo's footprint (still right-aligned) so the balls stay fully visible+clickable.
        // Footprint mirrors OrientationGizmo: center.Y = imageMin.Y + (radius=34 + 14)*S, bottom of the
        // hover ring = center.Y + (radius=34 + 8)*S = imageMin.Y + 90*S; +a small gap for clearance.
        const float gizmoBottom = (34f + 14f) + (34f + 8f);   // 90 px (pre-scale), see OrientationGizmo.cs:24-25,34
        float eyeMenuY = imageMin.Y + gizmoBottom * S + margin;
        EditorPrefs prefs = EditorPrefs.Current;
        ImGui.SetNextWindowPos(new SysVec2(imageMin.X + imageSize.X - margin, eyeMenuY),
            ImGuiCond.Always, new SysVec2(1f, 0f));   // pivot top-right, below the orientation gizmo
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

                ImGui.Separator();
                ImGui.TextDisabled("GI Debug");
                bool probes = ProbeRenderState.ProbeShowAll;
                string probeHint = probes
                    ? $" ({ProbeRenderState.ProbeOccupiedCount} occupied / {ProbeRenderState.ProbeTotalCount} total)" : "";
                if (ImGui.MenuItem($"{EditorIcons.ProbeLight}  Light Probes{probeHint}", (string)null, probes))
                    ProbeRenderState.ProbeShowAll = !probes;
                bool refl = ProbeRenderState.ReflectionShowAll;
                string reflHint = refl
                    ? $" ({ProbeRenderState.ReflectionCapturedCount} local / {ProbeRenderState.ReflectionTotalCount} total)" : "";
                if (ImGui.MenuItem($"{EditorIcons.ProbeReflection}  Reflection Probes{reflHint}", (string)null, refl))
                    ProbeRenderState.ReflectionShowAll = !refl;
                ImGui.EndPopup();
            }
        }
        ImGui.End();

        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
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
