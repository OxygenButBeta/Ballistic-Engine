using BallisticEngine.AssetPipeline;
using BallisticEngine.Bepu;
using BallisticEngine.Serialization;

namespace BallisticEngine;

public sealed class EngineBootstrap {
    public IBallisticEngineRuntime Runtime { get; }
    public BallisticProject Project { get; }

    public bool PlayerMode { get; }

    public EngineBootstrap(IBallisticEngineRuntime runtime, string projectPath,
                           bool deferAssetRefresh = false, bool playerMode = false) {
        Runtime = runtime;
        PlayerMode = playerMode;
        SystemAPI.Bind(runtime);

        SingleServiceInstaller.InstallAllInAssemblies(typeof(SceneManager).Assembly);

        Project = BallisticProject.Open(projectPath);

        if (!playerMode)
            JsonlLog.Start(Path.Combine(Project.LibraryPath, "Logs", "engine.jsonl"));

        System.Reflection.Assembly gameScripts = playerMode
            ? LoadPrebuiltGameScripts()
            : GameScripts.CompileAndLoad(Project);

        BuildComponentRegistry(gameScripts);

        AssetDatabase.Initialize(Project);

        string projectName = new DirectoryInfo(Project.RootPath).Name;
        SaveSystem.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ballistic", projectName, "Saves"));

        FalcorSceneImporter.Converter = (pyscene, output) =>
            FalcorSceneConverter.Convert(pyscene, output, ResolveModelToAssetRef);

        BlendImporter.Converter = (blend, fbx, json, output) =>
            BlendSceneConverter.Convert(blend, fbx, json, output, ResolveModelToAssetRef);

        if (playerMode) {
            MountContentPack();
            AssetDatabase.LoadFromArtifacts();
        }
        else if (!deferAssetRefresh) {
            AssetDatabase.Refresh();
        }

        SceneManager.SnapshotProvider = SceneSerializer.Serialize;
        SceneManager.SnapshotRestorer = (_, yaml) => SceneSerializer.Deserialize(yaml);

        SceneManager.SceneLoader = LoadSceneText;
        SceneManager.BuildScenes = ResolveBuildScenes();

        UI.UIDocument.TextResolver = path => ContentText.Read(Project, path);

        RegisterUIFonts();

        Audio.Backend ??= new BallisticEngine.OpenALAudio.OpenALBackend();

        Physics.World ??= new BepuPhysicsWorld();

        Network.Manager ??= new NetworkManager();

        LayerSettings.Load(Project);
        Physics.World.LayerCollisionMatrix = LayerManager.ShouldCollide;

        SceneManager.PlayBlocked = () => GameScripts.CompileFailed
            ? "game scripts have compile errors (see Console); play unlocks on the next successful compile."
            : null;

        runtime.RenderAsset.Initialize();
    }

    public void RefreshAssets() => AssetDatabase.Refresh();

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

    public static Func<IEnumerable<System.Reflection.Assembly>> ExtraScanAssemblies;

    void BuildComponentRegistry(System.Reflection.Assembly gameScripts) {
        var list = new List<System.Reflection.Assembly> { typeof(SceneManager).Assembly, Runtime.GetType().Assembly };
        if (gameScripts is not null)
            list.Add(gameScripts);
        if (ExtraScanAssemblies?.Invoke() is { } extra)
            foreach (System.Reflection.Assembly a in extra)
                if (a is not null) list.Add(a);
        System.Reflection.Assembly[] assemblies = list.ToArray();
        ComponentRegistry.Build(assemblies);

        TypeCache.Build(assemblies);

        BallisticEngine.InputSystem.InputRegistry.ScanForActions(assemblies);
    }

    public bool ReloadGameScripts() {
        if (!GameScripts.TryCompile(Project, out var assemblyPath, out var rebuilt))
            return false;

        if (!rebuilt && (assemblyPath is null || GameScripts.LoadedAssembly is not null))
            return true;

        var live = SceneManager.IsPlaying;
        Scene scene = SceneManager.GetCurrentScene();
        var snapshot = SceneSerializer.Serialize(scene);

        if (live) {
            scene.FireEnd();
            SceneManager.RenderCamera = null;
            RuntimeSet<IStaticMeshRenderer>.Clear();
        }
        scene.Clear();
        if (live) {
            Physics.EndPlay();
            DirectionalLight.Clear();
        }
        VolumeManager.ResetStack();
        RenderFeatureManager.Reset();

        BallisticEngine.InputSystem.InputRegistry.ClearForReload();
        NetworkReplicationRegistry.ClearForReload();
        SceneReplicationRegistry.ClearForReload();
        ReloadCaches.InvalidateAll();
        Network.Manager?.Stop();
        GameScripts.Unload();

        System.Reflection.Assembly gameScripts =
            assemblyPath is null ? null : GameScripts.LoadFrom(assemblyPath);
        BuildComponentRegistry(gameScripts);

        if (live)
            Physics.BeginPlay();
        SceneSerializer.Deserialize(snapshot);
        if (live) {
            if (GamePhaseRunner.HasGameMode(scene))
                GamePhaseRunner.Run(scene);
            else
                scene.FireBegin();
            Debugging.Log("Game scripts: live-reloaded while playing (serializable state preserved).");
        }
        return true;
    }

    string ResolveModelToAssetRef(string absoluteModelPath) {
        if (!File.Exists(absoluteModelPath))
            return null;

        var full = Path.GetFullPath(absoluteModelPath);
        if (!full.StartsWith(Project.RootPath, StringComparison.OrdinalIgnoreCase))
            return null;

        return Project.ToAssetPath(full);
    }

    public void UpdateFrame(double delta) {
        Runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);
        ParticleSystem.AdvanceAll((float)delta);
        TrailRenderer.AdvanceAll((float)delta);
        Audio.Update();
        InputActions.Update();
    }

    public void LoadStartupScene() {
        var startup = SceneManager.BuildScenes.Count > 0
            ? SceneManager.BuildScenes[0]
            : Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(startup))
            return;

        LoadSceneText(startup);
    }

    void LoadSceneText(string assetPath) {
        var yaml = ContentText.Read(Project, assetPath);
        if (yaml is null) {
            Debugging.LogError($"Scene '{assetPath}' not found (no loose file or content-pack entry).");
            return;
        }

        Scene current = SceneManager.GetCurrentScene();
        current.Clear();
        SceneManager.ClearAllRenderSets();
        SceneSerializer.Deserialize(yaml);
    }

    List<string> ResolveBuildScenes() {
        if (Project.Manifest.ScenesInBuild is { Count: > 0 } scenes)
            return scenes.Where(s => !string.IsNullOrEmpty(s)).ToList();
        return string.IsNullOrEmpty(Project.Manifest.StartupScene)
            ? []
            : [Project.Manifest.StartupScene];
    }

    System.Reflection.Assembly LoadPrebuiltGameScripts() {
        var dll = Path.Combine(Project.LibraryPath, "ScriptAssemblies", GameScripts.AssemblyName + ".dll");
        return File.Exists(dll) ? GameScripts.LoadFrom(dll) : null;
    }

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
