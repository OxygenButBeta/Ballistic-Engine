using System.Diagnostics;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor;

internal sealed class BuildPanel : EditorWindow {
    readonly BallisticProject project;

    readonly List<string> scenes = new();
    bool initialized;

    string productName = "";
    string companyName = "";
    string version = "1.0.0";
    string iconPath = "";
    int windowModeIndex;
    int resWidth = 1920, resHeight = 1080;

    string outputDir = "";
    bool selfContained = true;
    int configIndex;
    int ridIndex;
    static readonly string[] Configurations = { "Release", "Debug" };
    static readonly string[] Rids = { "win-x64", "win-arm64" };
    static readonly string[] WindowModes = { "Fullscreen", "Windowed", "Borderless" };

    bool runAfterBuild;
    bool openFolderAfterBuild = true;

    readonly object gate = new();
    readonly List<string> log = new();
    bool building;
    bool? lastSucceeded;
    string lastSummary;
    string lastExePath;
    volatile bool runWhenDone;
    volatile bool openWhenDone;
    string pendingLaunchExe;

    public BuildPanel(BallisticProject project) {
        this.project = project;
        DockKey = "win.build";
        Title = "Build";
        Icon = EditorIcons.Package;
        DesiredSize = new Vector2(580, 640);
    }

    protected override void OnGui(IEditorGui gui) {
        EnsureInitialized();

        if (pendingLaunchExe is not null) {
            LaunchBuiltExe(pendingLaunchExe);
            pendingLaunchExe = null;
        }

        float scale = gui.Scale;
        DrawPlayerSettings(gui);
        gui.Dummy(new Vector2(0, 4 * scale));
        DrawScenesInBuild(gui);
        gui.Dummy(new Vector2(0, 4 * scale));
        DrawOutputSection(gui);
        gui.Dummy(new Vector2(0, 6 * scale));
        DrawBuildButton(gui);
        DrawLog(gui);
    }

    void EnsureInitialized() {
        if (initialized)
            return;
        initialized = true;

        if (project.Manifest.ScenesInBuild is { Count: > 0 } saved)
            scenes.AddRange(saved.Where(s => !string.IsNullOrEmpty(s)));
        else if (!string.IsNullOrEmpty(project.Manifest.StartupScene))
            scenes.Add(project.Manifest.StartupScene);

        PlayerSettings p = PlayerSettings.OrDefault(project.Manifest);
        productName = p.ProductName ?? project.Manifest.Name;
        companyName = p.CompanyName ?? "";
        version = string.IsNullOrWhiteSpace(p.Version) ? "1.0.0" : p.Version;
        iconPath = p.IconPath ?? "";
        windowModeIndex = (int)p.WindowMode;
        resWidth = p.Width > 0 ? p.Width : 1920;
        resHeight = p.Height > 0 ? p.Height : 1080;
        selfContained = p.SelfContained;
        configIndex = Array.IndexOf(Configurations, p.Configuration); if (configIndex < 0) configIndex = 0;
        ridIndex = Array.IndexOf(Rids, p.RuntimeIdentifier); if (ridIndex < 0) ridIndex = 0;

        outputDir = Path.Combine(project.RootPath, "Build", Sanitize(productName));
    }

    void DrawPlayerSettings(IEditorGui gui) {
        float scale = gui.Scale;
        if (!gui.CollapsingHeader($"{EditorIcons.Wrench}  Player Settings", defaultOpen: true))
            return;

        gui.Indent(8 * scale);

        LabeledInput(gui, "Product Name", "##product", ref productName, 128,
            "The shipped game's name: the window title AND the published <Name>.exe.");
        LabeledInput(gui, "Company", "##company", ref companyName, 128,
            "Embedded in the exe's file details (optional).");
        LabeledInput(gui, "Version", "##version", ref version, 32,
            "Version baked into the exe (e.g. 1.0.0). Free-form text also allowed.");

        gui.TextDisabled("Icon (.ico)");
        if (gui.IsItemHovered())
            gui.Tooltip("A .ico embedded into the exe for its taskbar/file icon. Optional.");
        float btns = (EditorIcons.SmallButtonWidth(EditorIcons.Folder) + gui.ItemSpacing.X) * 2;
        var iconDisplay = string.IsNullOrEmpty(iconPath) ? "(engine default)" : iconPath;
        gui.SetNextItemWidth(gui.ContentRegionAvail.X - btns);
        gui.BeginDisabled(true);
        gui.InputText("##icon", ref iconDisplay, 512);
        gui.EndDisabled();
        gui.SameLine();
        if (EditorIcons.GhostButtonSmall("pickicon", EditorIcons.Folder, "Pick a .ico file")) BrowseForIcon();
        gui.SameLine();
        gui.BeginDisabled(string.IsNullOrEmpty(iconPath));
        if (EditorIcons.GhostButtonSmall("clearicon", EditorIcons.Delete, "Use the engine default icon")) iconPath = "";
        gui.EndDisabled();

        gui.TextDisabled("Window");
        gui.SetNextItemWidth(160 * scale);
        gui.Combo("Mode##winmode", ref windowModeIndex, WindowModes);
        if (gui.IsItemHovered())
            gui.Tooltip("Fullscreen = borderless at the monitor's resolution.\n" +
                        "Windowed/Borderless use the resolution below.");

        gui.BeginDisabled(windowModeIndex == 0);
        gui.SetNextItemWidth(90 * scale);
        gui.InputInt("##resw", ref resWidth, 0);
        gui.SameLine();
        gui.TextDisabled("x");
        gui.SameLine();
        gui.SetNextItemWidth(90 * scale);
        gui.InputInt("##resh", ref resHeight, 0);
        gui.SameLine();
        gui.TextDisabled("default window size");
        gui.EndDisabled();
        resWidth = Math.Clamp(resWidth, 320, 16384);
        resHeight = Math.Clamp(resHeight, 240, 16384);

        gui.Unindent(8 * scale);
    }

    static void LabeledInput(IEditorGui gui, string label, string id, ref string value, int max, string tip) {
        gui.TextDisabled(label);
        if (tip is not null && gui.IsItemHovered())
            gui.Tooltip(tip);
        gui.SetNextItemWidth(gui.ContentRegionAvail.X);
        gui.InputText(id, ref value, max);
    }

    void BrowseForIcon() {
        string picked = NativeDialogs.PickFile("Select an application icon", "Icon", new[] { "ico" },
            project.AssetsPath);
        if (string.IsNullOrEmpty(picked))
            return;
        if (picked.StartsWith(project.RootPath, StringComparison.OrdinalIgnoreCase))
            iconPath = Path.GetRelativePath(project.RootPath, picked).Replace('\\', '/');
        else
            iconPath = picked;
    }

    void DrawScenesInBuild(IEditorGui gui) {
        float scale = gui.Scale;
        if (!gui.CollapsingHeader($"{EditorIcons.Document}  Scenes In Build", defaultOpen: true))
            return;
        if (gui.IsItemHovered())
            gui.Tooltip("Scenes shipped with the build. The first one loads on startup.\n" +
                        "Load others at runtime with SceneManager.LoadScene(\"Name\").");

        gui.BeginChild("##scenes", new Vector2(0, 150 * scale), border: true);
        if (scenes.Count == 0)
            gui.TextDisabled("No scenes added. Use \"Add Open Scene\" or the + below.");

        int moveFrom = -1, moveTo = -1, remove = -1;
        for (int i = 0; i < scenes.Count; i++) {
            gui.PushId(i);

            if (i == 0) {
                gui.TextColored(EditorIcons.TintLight, EditorIcons.Home);
                if (gui.IsItemHovered()) gui.Tooltip("Startup scene");
                gui.SameLine();
            }
            else {
                gui.Dummy(new Vector2(gui.CalcTextSize(EditorIcons.Home).X, 0));
                gui.SameLine();
            }

            gui.TextDisabled($"{i}");
            gui.SameLine();
            gui.TextUnformatted(SceneName(scenes[i]));
            gui.SameLine();
            gui.TextDisabled($"  {scenes[i]}");

            gui.SameLine();
            float bw = EditorIcons.SmallButtonWidth(EditorIcons.ChevronDown);
            float gap = gui.ItemSpacing.X;
            gui.CursorPosX += gui.ContentRegionAvail.X - bw * 3 - gap * 2;
            gui.BeginDisabled(i == 0);
            if (EditorIcons.GhostButtonSmall("up", EditorIcons.ChevronRight, "Move up")) { moveFrom = i; moveTo = i - 1; }
            gui.EndDisabled();
            gui.SameLine();
            gui.BeginDisabled(i == scenes.Count - 1);
            if (EditorIcons.GhostButtonSmall("down", EditorIcons.ChevronDown, "Move down")) { moveFrom = i; moveTo = i + 1; }
            gui.EndDisabled();
            gui.SameLine();
            if (EditorIcons.GhostButtonSmall("rm", EditorIcons.Delete, "Remove")) remove = i;

            gui.PopId();
        }
        gui.EndChild();

        if (remove >= 0) scenes.RemoveAt(remove);
        if (moveFrom >= 0 && moveTo >= 0 && moveFrom < scenes.Count && moveTo < scenes.Count)
            (scenes[moveFrom], scenes[moveTo]) = (scenes[moveTo], scenes[moveFrom]);

        if (gui.Button($"{EditorIcons.Add}  Add Open Scene")) AddOpenScene(gui);
        if (gui.IsItemHovered()) gui.Tooltip("Add the currently loaded scene");
        gui.SameLine();
        if (gui.Button($"{EditorIcons.Folder}  Add Scene...")) gui.OpenPopup("##addscene");
        gui.SameLine();
        if (gui.Button($"{EditorIcons.Save}  Save Settings")) SaveManifest();
        if (gui.IsItemHovered()) gui.Tooltip("Write scenes + player settings into project.json without building");

        DrawAddScenePopup(gui);
    }

    void DrawAddScenePopup(IEditorGui gui) {
        float scale = gui.Scale;
        gui.SetNextWindowSizeAppearing(new Vector2(360 * scale, 360 * scale));
        if (!gui.BeginPopup("##addscene"))
            return;

        gui.TextDisabled("Add a scene to the build");
        gui.Separator();
        gui.BeginChild("##scenelist", default, border: false);
        foreach (string path in AllScenePaths()) {
            bool already = scenes.Contains(path, StringComparer.OrdinalIgnoreCase);
            gui.BeginDisabled(already);
            var (icon, tint) = EditorIcons.ForAssetExtension(".scene");
            gui.TextColored(tint, icon);
            gui.SameLine();
            if (gui.Selectable($"{SceneName(path)}##{path}", false)) {
                scenes.Add(path);
                gui.CloseCurrentPopup();
            }
            gui.SameLine();
            gui.TextDisabled(already ? "  (in build)" : $"  {path}");
            gui.EndDisabled();
        }
        gui.EndChild();
        gui.EndPopup();
    }

    void DrawOutputSection(IEditorGui gui) {
        float scale = gui.Scale;
        if (!gui.CollapsingHeader($"{EditorIcons.Folder}  Output", defaultOpen: true))
            return;

        gui.Indent(8 * scale);
        gui.TextDisabled("Output Folder");

        float browseW = gui.CalcTextSize($"{EditorIcons.Folder}  Browse...").X + gui.FramePadding.X * 2;
        gui.SetNextItemWidth(gui.ContentRegionAvail.X - browseW - gui.ItemSpacing.X);
        gui.InputText("##out", ref outputDir, 512);
        gui.SameLine();
        gui.BeginDisabled(building);
        if (gui.Button($"{EditorIcons.Folder}  Browse..."))
            BrowseForOutput();
        gui.EndDisabled();

        gui.Checkbox("Self-contained (bundle .NET 9 — target needs no runtime installed)", ref selfContained);

        gui.SetNextItemWidth(140 * scale);
        gui.Combo("Configuration", ref configIndex, Configurations);
        gui.SameLine();
        gui.SetNextItemWidth(140 * scale);
        gui.Combo("Platform", ref ridIndex, Rids);
        if (gui.IsItemHovered())
            gui.Tooltip("Target runtime. win-x64 for most PCs; win-arm64 for ARM Windows.");

        gui.Unindent(8 * scale);
    }

    void BrowseForOutput() {
        string seed = outputDir;
        while (!string.IsNullOrEmpty(seed) && !Directory.Exists(seed))
            seed = Path.GetDirectoryName(seed);
        if (string.IsNullOrEmpty(seed))
            seed = project.RootPath;

        string picked = NativeDialogs.PickFolder("Select build output folder", seed);
        if (!string.IsNullOrEmpty(picked))
            outputDir = picked;
    }

    void DrawBuildButton(IEditorGui gui) {
        float scale = gui.Scale;
        gui.Separator();
        gui.Checkbox("Run after build", ref runAfterBuild);
        gui.SameLine(0, 24 * scale);
        gui.Checkbox("Open output folder when done", ref openFolderAfterBuild);

        bool canBuild = !building && scenes.Count > 0 && !string.IsNullOrWhiteSpace(outputDir)
                        && !string.IsNullOrWhiteSpace(productName);

        gui.BeginDisabled(!canBuild);
        if (gui.Button(building ? "Building..." : $"{EditorIcons.Package}  Build",
                       new Vector2(160 * scale, 32 * scale)))
            StartBuild();
        gui.EndDisabled();

        if (scenes.Count == 0) {
            gui.SameLine();
            gui.TextColored(EditorIcons.TintLight, "Add at least one scene to build.");
        }
        else if (string.IsNullOrWhiteSpace(productName)) {
            gui.SameLine();
            gui.TextColored(EditorIcons.TintLight, "Set a Product Name.");
        }
        else if (lastSucceeded == true) {
            gui.SameLine();
            if (gui.Button($"{EditorIcons.FolderOpen}  Open Folder", new Vector2(0, 32 * scale)))
                OpenOutputFolder();
            if (lastExePath is not null && File.Exists(lastExePath)) {
                gui.SameLine();
                if (gui.Button($"{EditorIcons.Play}  Run", new Vector2(0, 32 * scale)))
                    LaunchBuiltExe(lastExePath);
            }
        }
    }

    void DrawLog(IEditorGui gui) {
        float scale = gui.Scale;
        gui.Dummy(new Vector2(0, 6 * scale));
        gui.Separator();

        bool? ok;
        string summary;
        string[] lines;
        lock (gate) {
            ok = lastSucceeded;
            summary = lastSummary;
            lines = log.ToArray();
        }

        if (summary is not null) {
            Vector4 color = ok == true ? EditorTheme.Success
                          : ok == false ? EditorTheme.Error
                          : EditorIcons.TintGeneric;
            gui.TextColored(color, summary);
        }

        gui.BeginChild("##buildlog", new Vector2(0, 0), border: true, horizontalScroll: true);
        foreach (string line in lines)
            gui.TextUnformatted(line);
        if (building && gui.ScrollY >= gui.ScrollMaxY - 4)
            gui.SetScrollHereY(1f);
        gui.EndChild();
    }

    void StartBuild() {
        PlayerSettings player = CollectPlayerSettings();
        var options = new BuildPipeline.Options {
            Project = project,
            OutputDir = outputDir.Trim(),
            ScenesInBuild = scenes.ToList(),
            SelfContained = selfContained,
            Configuration = Configurations[configIndex],
            RuntimeIdentifier = Rids[ridIndex],
            Player = player,
        };

        SaveManifest();

        runWhenDone = runAfterBuild;
        openWhenDone = openFolderAfterBuild;

        lock (gate) {
            log.Clear();
            building = true;
            lastSucceeded = null;
            lastSummary = "Building...";
            lastExePath = null;
        }
        BuildProgress.Begin();

        var thread = new Thread(() => {
            BuildPipeline.Result result = BuildPipeline.Build(options, Append);
            lock (gate) {
                building = false;
                lastSucceeded = result.Success;
                lastExePath = result.Success ? result.ExePath : null;
                lastSummary = result.Success
                    ? $"{EditorIcons.Check}  Build succeeded  ({Megabytes(result.TotalBytes)} MB)."
                    : $"{EditorIcons.Error}  Build failed: {result.Error}";
                if (!result.Success && result.Error is not null)
                    log.Add(result.Error);
            }
            BuildProgress.End();

            if (result.Success) {
                if (openWhenDone)
                    OpenFolder(result.OutputDir);
                if (runWhenDone && result.ExePath is not null)
                    pendingLaunchExe = result.ExePath;
            }
        }) { IsBackground = true, Name = "BallisticBuild" };
        thread.Start();
    }

    PlayerSettings CollectPlayerSettings() => new() {
        ProductName = string.IsNullOrWhiteSpace(productName) ? project.Manifest.Name : productName.Trim(),
        CompanyName = companyName.Trim(),
        Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim(),
        IconPath = string.IsNullOrWhiteSpace(iconPath) ? null : iconPath.Trim(),
        WindowMode = (WindowMode)windowModeIndex,
        Width = resWidth,
        Height = resHeight,
        Configuration = Configurations[configIndex],
        RuntimeIdentifier = Rids[ridIndex],
        SelfContained = selfContained,
    };

    void Append(string message) {
        lock (gate) log.Add(message);

        if (message.StartsWith(' '))
            BuildProgress.Detail = message.Trim();
        else
            BuildProgress.Step(message);
    }

    void AddOpenScene(IEditorGui gui) {
        var current = scenes.Count == 0 ? project.Manifest.StartupScene : null;
        if (!string.IsNullOrEmpty(current) && !scenes.Contains(current, StringComparer.OrdinalIgnoreCase))
            scenes.Add(current);
        else if (scenes.Count > 0 || string.IsNullOrEmpty(current))
            gui.OpenPopup("##addscene");
    }

    void SaveManifest() {
        project.Manifest.ScenesInBuild = scenes.ToList();
        project.Manifest.StartupScene = scenes.FirstOrDefault();
        project.Manifest.Player = CollectPlayerSettings();
        PipelineJson.Write(Path.Combine(project.RootPath, "project.json"), project.Manifest);
        Append($"Saved build settings to project.json ({scenes.Count} scene(s)).");
    }

    void OpenOutputFolder() => OpenFolder(outputDir);

    static void OpenFolder(string dir) {
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            try { Process.Start("explorer.exe", $"\"{dir}\""); } catch {
            }
    }

    void LaunchBuiltExe(string exePath) {
        try {
            Process.Start(new ProcessStartInfo(exePath) {
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = true,
            });
            Append($"Launched {Path.GetFileName(exePath)}.");
        }
        catch (Exception e) {
            Append($"Could not launch {Path.GetFileName(exePath)}: {e.Message}");
        }
    }

    IEnumerable<string> AllScenePaths() =>
        AssetDatabase.EnumerateAssets()
            .Select(kv => kv.Key)
            .Where(p => p.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    static string SceneName(string assetPath) => Path.GetFileNameWithoutExtension(assetPath);

    static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1");

    static string Sanitize(string name) {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Game" : name;
    }
}
