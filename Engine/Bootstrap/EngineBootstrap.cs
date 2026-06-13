using BallisticEngine.AssetPipeline;
using BallisticEngine.Bepu;
using BallisticEngine.Serialization;

namespace BallisticEngine;

// Brings the engine up to a runnable state for any host (runtime player or editor):
//   bind runtime services -> install [EngineService]s -> open project + import assets -> init renderer.
//
// It does NOT wire the update/render loop or load any scene content — the host decides
// how to drive frames (BEngineEntry for the player, EditorApplication for the editor).
public sealed class EngineBootstrap {
    public IBallisticEngineRuntime Runtime { get; }
    public BallisticProject Project { get; }

    // True for a shipped standalone player: assets are PRE-BAKED in Library\ and the .NET SDK is
    // not assumed present, so skip `dotnet build` (load the pre-built GameScripts.dll directly) and
    // skip the asset Refresh re-import (the editor baked everything at build time). See BuildPipeline.
    public bool PlayerMode { get; }

    // deferAssetRefresh: when true, the constructor skips the (potentially slow) asset import so the
    // host can open its window first and run the refresh asynchronously behind a busy UI — the editor
    // does this. The host MUST then call RefreshAssets() before loading any scene. Default false
    // keeps the player's behavior: assets are imported synchronously before the constructor returns.
    //
    // playerMode: see PlayerMode — set true only for a shipped build with a pre-baked Library\.
    public EngineBootstrap(IBallisticEngineRuntime runtime, string projectPath,
                           bool deferAssetRefresh = false, bool playerMode = false) {
        Runtime = runtime;
        PlayerMode = playerMode;
        SystemAPI.Bind(runtime);

        // [EngineService] types (SceneManager, EngineConfigurationAsset) live in THIS library,
        // not the host exe — scan the engine assembly, not the entry assembly.
        SingleServiceInstaller.InstallAllInAssemblies(typeof(SceneManager).Assembly);

        Project = BallisticProject.Open(projectPath);

        // Structured log mirror for agents/tools: Library/Logs/engine.jsonl (editable projects
        // only — a shipped player must not write into its install folder).
        if (!playerMode)
            JsonlLog.Start(Path.Combine(Project.LibraryPath, "Logs", "engine.jsonl"));

        // Load the project's C# game scripts. In the editor/dev runtime this compiles via `dotnet build`
        // first; in a shipped player the SDK may be absent, so load the pre-built GameScripts.dll as-is.
        // Null when the project has no scripts or they failed to compile (errors are in the log).
        System.Reflection.Assembly gameScripts = playerMode
            ? LoadPrebuiltGameScripts()
            : GameScripts.CompileAndLoad(Project);

        // Discover Behaviour types for scene (de)serialization and the editor's Add Component menu:
        // the engine assembly, the host (may define its own components), and the game scripts.
        BuildComponentRegistry(gameScripts);

        AssetDatabase.Initialize(Project);

        // Persistent game saves (PlayerPrefs / SaveData) live under the OS user-data folder, keyed by
        // project name — NOT in the project source tree, so saves never get committed (Unity's
        // persistentDataPath). %AppData%/Ballistic/<ProjectName>/Saves on Windows.
        string projectName = new DirectoryInfo(Project.RootPath).Name;
        SaveSystem.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ballistic", projectName, "Saves"));

        // Falcor .pyscene -> Ballistic .scene conversion (injected; the converter is in the Engine layer).
        FalcorSceneImporter.Converter = (pyscene, output) =>
            FalcorSceneConverter.Convert(pyscene, output, ResolveModelToAssetRef);

        // Blender .blend -> sibling .fbx (meshes) + .scene (camera/lights) conversion (injected;
        // the JSON->SceneDocument converter is in the Engine layer, same pattern as Falcor).
        BlendImporter.Converter = (blend, fbx, json, output) =>
            BlendSceneConverter.Convert(blend, fbx, json, output, ResolveModelToAssetRef);

        // pbrt .pbrt (v3/v4) -> Ballistic .scene + sibling .mat files (injected; same pattern). The
        // resolver maps referenced .ply geometry and textures (inside the project) to "Assets/..." refs.
        PbrtSceneImporter.Converter = (pbrt, output) =>
            PbrtSceneConverter.Convert(pbrt, output, ResolveModelToAssetRef);

        // A shipped player ships pre-baked content and must NOT re-import from source (sources aren't
        // even present, and there is no SDK). It mounts the content pack (artifacts + scene/material
        // text), then loads the GUID lookup tables from the loose baked metadata — without which every
        // scene asset ref fails to resolve (symptom: empty scene, just sky). The editor and dev runtime
        // do a full refresh (deferred or not) and read loose files.
        if (playerMode) {
            MountContentPack();
            AssetDatabase.LoadFromArtifacts();
        }
        else if (!deferAssetRefresh) {
            AssetDatabase.Refresh();
        }

        // Play/Stop uses the scene serializer to snapshot edit-mode state and restore it.
        SceneManager.SnapshotProvider = SceneSerializer.Serialize;
        SceneManager.SnapshotRestorer = (_, yaml) => SceneSerializer.Deserialize(yaml);

        // Unity-style runtime scene loading (SceneManager.LoadScene): wire the loader + build list
        // from the manifest. Loader reads the project-relative .scene — pack-aware (ContentText), so a
        // shipped player loads it from the mounted content pack — over the cleared current scene.
        SceneManager.SceneLoader = LoadSceneText;
        SceneManager.BuildScenes = ResolveBuildScenes();

        // Game UI: let a UIDocument resolve its .uxml/.uss asset paths to text (pack-aware, same as
        // scenes). The UI layer stays free of AssetPipeline — it only sees this delegate.
        UI.UIDocument.TextResolver = path => ContentText.Read(Project, path);

        // Provide UI fonts (CPU SDF atlases) for text rendering. The GL backend uploads them; the UI
        // layer never touches GL or the font baker. Registers every .ttf under Assets/UI/Fonts/ by its
        // file name (so `font-family: 'Cinzel'` resolves Cinzel.ttf), and sets a default.
        RegisterUIFonts();

        // Audio backend (OpenAL), composition-root wiring like physics/renderer: components only
        // ever see IAudioBackend. Initializes the output device now; degrades to silence (logged)
        // if no device/driver is present (headless CI), never crashing.
        Audio.Backend ??= new BallisticEngine.OpenALAudio.OpenALBackend();

        // Physics backend (Bepu), composition-root wiring like the renderer below: components
        // only ever see IPhysicsWorld. The simulation runs in play mode, driven by SceneManager.
        Physics.World ??= new BepuPhysicsWorld();

        // Inject the layer collision matrix so the backend filters contacts by layer without
        // referencing the Engine layer's LayerManager directly (same delegate pattern as above).
        // Load the project's tag/layer settings first so the matrix and names are authoritative.
        LayerSettings.Load(Project);
        Physics.World.LayerCollisionMatrix = LayerManager.ShouldCollide;

        // Unity's "fix compile errors before entering playmode": StartPlay refuses while the
        // latest script compile failed. Injected here so the Engine layer stays free of
        // AssetPipeline knowledge.
        SceneManager.PlayBlocked = () => GameScripts.CompileFailed
            ? "game scripts have compile errors (see Console); play unlocks on the next successful compile."
            : null;

        runtime.RenderAsset.Initialize();
    }

    // Imports/refreshes the project's assets. Hosts that deferred the refresh (the editor) call this
    // once the window is up — typically asynchronously, behind a busy indicator — before loading a
    // scene. Safe to call again later for a manual re-import.
    public void RefreshAssets() => AssetDatabase.Refresh();

    // Bakes + registers UI fonts (CPU SDF atlases). Registers every .ttf found anywhere under the
    // project's Assets\ by its file-name family (so `font-family: 'Cinzel'` resolves Cinzel.ttf). Picks
    // a default: Assets/UI/Default.ttf, else the first registered font, else the engine-bundled font.
    // The atlases are CPU-only; the GL backend uploads them.
    void RegisterUIFonts() {
        var assetsRoot = Project.ResolveAbsolute("Assets");
        if (Directory.Exists(assetsRoot)) {
            foreach (var ttf in Directory.EnumerateFiles(assetsRoot, "*.ttf", SearchOption.AllDirectories)) {
                var family = Path.GetFileNameWithoutExtension(ttf);
                var atlas = FontBaker.Bake(ttf);
                if (atlas != null)
                    UI.UIFonts.Register(family, atlas);
            }
        }

        // Default: explicit Assets/UI/Default.ttf wins; else the engine-bundled font; else any
        // registered font (so text still renders).
        var explicitDefault = Project.ResolveAbsolute("Assets/UI/Default.ttf");
        if (File.Exists(explicitDefault))
            UI.UIFonts.Default = FontBaker.Bake(explicitDefault);
        else {
            var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Inter-Regular.ttf");
            if (File.Exists(bundled))
                UI.UIFonts.Default = FontBaker.Bake(bundled);
            else if (UI.UIFonts.All.Count > 0) {
                foreach (var kv in UI.UIFonts.All) { UI.UIFonts.Default = kv.Value; break; }
            }
        }

        if (UI.UIFonts.Default is null)
            Debugging.LogWarning("No UI font found; UI text will not render until one is provided.");
    }

    void BuildComponentRegistry(System.Reflection.Assembly gameScripts) {
        ComponentRegistry.Build(gameScripts is null
            ? [typeof(SceneManager).Assembly, Runtime.GetType().Assembly]
            : [typeof(SceneManager).Assembly, Runtime.GetType().Assembly, gameScripts]);
    }

    // Rebuilds the project's script assembly and reloads the current scene over the new types
    // (the editor's "Rebuild Scripts" / focus-regain auto-compile). Compile-first: on compiler
    // errors nothing changes — the already-loaded scripts and the scene stay untouched. Returns
    // false only on compile failure.
    //
    // The reload itself is a domain-reload in miniature: snapshot the scene to YAML, clear it
    // (detaching every component so renderers leave their draw sets), drop every cache that pins
    // old script types (registry, volume stack), unload the old AssemblyLoadContext, load the new
    // assembly, then rebuild the scene from the snapshot — component names in the YAML resolve to
    // the NEW types through the rebuilt registry.
    //
    // LIVE reload ("unlike Unity"): while playing, the swap preserves the RUNNING game — the
    // serialized snapshot is the LIVE scene (play-mode spawns and mutated values included), play
    // never stops, and lifecycle restarts on the new types via FireBegin with those live values.
    // Runtime-only state (non-serialized fields, physics velocities) restarts from OnBegin — the
    // accepted cost. SceneManager's pre-play snapshot is untouched, so a later Stop still returns
    // to the edit-mode scene.
    public bool ReloadGameScripts() {
        if (!GameScripts.TryCompile(Project, out var assemblyPath, out var rebuilt))
            return false;

        // Nothing changed: no scripts at all, or the dll is current AND already loaded — skip
        // the scene-reload dance (the editor calls this on every window-focus regain).
        if (!rebuilt && (assemblyPath is null || GameScripts.LoadedAssembly is not null))
            return true;

        var live = SceneManager.IsPlaying;
        Scene scene = SceneManager.GetCurrentScene();
        var snapshot = SceneSerializer.Serialize(scene);

        // Tear down mirroring StopPlay (minus the IsPlaying flip and snapshot restore).
        if (live) {
            scene.FireEnd();
            SceneManager.RenderCamera = null;
            RuntimeSet<IStaticMeshRenderer>.Clear();
        }
        scene.Clear();
        if (live) {
            Physics.EndPlay();  // after Clear so component teardown saw a live world
            DirectionalLight.Clear();
        }
        VolumeManager.ResetStack();
        GameScripts.Unload();

        System.Reflection.Assembly gameScripts =
            assemblyPath is null ? null : GameScripts.LoadFrom(assemblyPath);
        BuildComponentRegistry(gameScripts);

        // Bring-up mirroring StartPlay: fresh physics world BEFORE components re-create bodies.
        if (live)
            Physics.BeginPlay();
        SceneSerializer.Deserialize(snapshot);  // play lifecycle suppressed inside
        if (live) {
            scene.FireBegin();
            Debugging.Log("Game scripts: live-reloaded while playing (serializable state preserved).");
        }
        return true;
    }

    // Maps an absolute model path (from a .pyscene) to an "Assets/..." reference if it lives in the
    // project. Returns a path ref (not a guid) because the model's GUID may not be assigned yet during
    // the same refresh; the path resolves at scene-load time once the refresh completes.
    string ResolveModelToAssetRef(string absoluteModelPath) {
        if (!File.Exists(absoluteModelPath))
            return null;

        var full = Path.GetFullPath(absoluteModelPath);
        if (!full.StartsWith(Project.RootPath, StringComparison.OrdinalIgnoreCase))
            return null;

        return Project.ToAssetPath(full);
    }

    // Advances the engine one frame: ticks the clock and updates the scene (scene Update is a
    // no-op unless playing). Hosts that drive their own loop (the editor) call this.
    public void UpdateFrame(double delta) {
        Runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);
        ParticleSystem.AdvanceAll((float)delta);   // once per frame, edit + play (editor preview)
        TrailRenderer.AdvanceAll((float)delta);
        Audio.Update();
        InputActions.Update();   // snapshot action down-state for next frame's press/release edges
    }

    // Loads the project's startup scene (ScenesInBuild[0], or the legacy StartupScene field when the
    // build list is empty) into the current scene, in edit mode.
    public void LoadStartupScene() {
        var startup = SceneManager.BuildScenes.Count > 0
            ? SceneManager.BuildScenes[0]
            : Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(startup))
            return;

        LoadSceneText(startup);
    }

    // Reads a scene's YAML (pack-aware via ContentText — loose file in the editor/dev, content pack in
    // a shipped player) and deserializes it into the current scene in edit mode. Wired as
    // SceneManager.SceneLoader for runtime LoadScene, and used by LoadStartupScene.
    void LoadSceneText(string assetPath) {
        var yaml = ContentText.Read(Project, assetPath);
        if (yaml is null) {
            Debugging.LogError($"Scene '{assetPath}' not found (no loose file or content-pack entry).");
            return;
        }
        // Tear down the OUTGOING scene before loading the new one — Deserialize ADDS to the current
        // scene, so without this the old entities' components stay registered (RuntimeSet renderers,
        // lights, ...) and keep drawing/leaking even though they're gone from the hierarchy. The
        // editor's scene-open path already does this (SceneCommands.ApplyNow); the runtime LoadScene
        // path was missing it — the "old meshes still render after switching scenes" bug.
        Scene current = SceneManager.GetCurrentScene();
        current.Clear();
        SceneManager.ClearAllRenderSets(); // defensive: scene.Clear()'s OnDetach is best-effort
        SceneSerializer.Deserialize(yaml);
    }

    // The ordered build-scene list (project-relative paths). Prefers the manifest's ScenesInBuild;
    // falls back to the single legacy StartupScene so older projects still load + can LoadScene it.
    List<string> ResolveBuildScenes() {
        if (Project.Manifest.ScenesInBuild is { Count: > 0 } scenes)
            return scenes.Where(s => !string.IsNullOrEmpty(s)).ToList();
        return string.IsNullOrEmpty(Project.Manifest.StartupScene)
            ? []
            : [Project.Manifest.StartupScene];
    }

    // Loads the pre-built GameScripts.dll without compiling (shipped player path — no .NET SDK).
    // Returns null when the project shipped no scripts (the dll is simply absent).
    System.Reflection.Assembly LoadPrebuiltGameScripts() {
        var dll = Path.Combine(Project.LibraryPath, "ScriptAssemblies", GameScripts.AssemblyName + ".dll");
        return File.Exists(dll) ? GameScripts.LoadFrom(dll) : null;
    }

    // Mounts the shipped content pack (Data\content.pak) so artifact + scene/material reads resolve
    // from it (ContentMount). Silent no-op if there's no pack (e.g. a dev project run with --player,
    // where the loose Library files serve everything instead).
    void MountContentPack() {
        var pak = Path.Combine(Project.RootPath, "content.pak");
        if (!File.Exists(pak))
            return;
        try {
            ContentMount.Mount(pak);
        }
        catch (Exception e) {
            Debugging.LogError($"Failed to mount content pack '{pak}': {e.Message}");
        }
    }
}
