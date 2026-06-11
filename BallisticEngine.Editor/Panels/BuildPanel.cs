using System.Diagnostics;
using BallisticEngine.AssetPipeline;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Build window (Window > Build) — Unity-style "Build Settings". An ordered "Scenes In Build" list
// (first = startup scene), an output folder, and a Build button that produces a shippable standalone
// player via BuildPipeline (publishes the Runtime exe self-contained + copies project content). The
// build runs on a worker thread behind a live log so the editor stays responsive during the minutes
// a self-contained publish takes.
internal sealed class BuildPanel {
    public bool Open;

    readonly BallisticProject project;

    // Ordered build scenes (project-relative "Assets/...scene"). Seeded from the manifest; edits here
    // are saved into project.json by the build (and by an explicit Save button).
    readonly List<string> scenes = new();
    bool initialized;

    string outputDir = "";
    bool selfContained = true;

    // ---- worker state (touched from the build thread, read on the Um thread under `gate`) ----
    readonly object gate = new();
    readonly List<string> log = new();
    bool building;
    bool? lastSucceeded;
    string lastSummary;

    public BuildPanel(BallisticProject project) {
        this.project = project;
    }

    public void Draw(float scale) {
        if (!Open)
            return;

        EnsureInitialized();

        ImGui.SetNextWindowSize(new SysVec2(560 * scale, 560 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{EditorIcons.Package}  Build", ref Open)) {
            ImGui.End();
            return;
        }

        DrawScenesInBuild(scale);
        ImGui.Dummy(new SysVec2(0, 6 * scale));
        DrawOutputSection(scale);
        ImGui.Dummy(new SysVec2(0, 6 * scale));
        DrawBuildButton(scale);
        DrawLog(scale);

        ImGui.End();
    }

    void EnsureInitialized() {
        if (initialized)
            return;
        initialized = true;

        if (project.Manifest.ScenesInBuild is { Count: > 0 } saved)
            scenes.AddRange(saved.Where(s => !string.IsNullOrEmpty(s)));
        else if (!string.IsNullOrEmpty(project.Manifest.StartupScene))
            scenes.Add(project.Manifest.StartupScene);

        outputDir = Path.Combine(project.RootPath, "Build", Sanitize(project.Manifest.Name));
    }

    // ---- Scenes In Build ----------------------------------------------------

    void DrawScenesInBuild(float scale) {
        ImGui.TextDisabled("Scenes In Build");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scenes shipped with the build. The first one loads on startup.\n" +
                             "Load others at runtime with SceneManager.LoadScene(\"Name\").");

        ImGui.BeginChild("##scenes", new SysVec2(0, 170 * scale), ImGuiChildFlags.Borders);
        if (scenes.Count == 0)
            ImGui.TextDisabled("No scenes added. Use \"Add Open Scene\" or the + below.");

        int moveFrom = -1, moveTo = -1, remove = -1;
        for (int i = 0; i < scenes.Count; i++) {
            ImGui.PushID(i);

            if (i == 0) {
                ImGui.TextColored(EditorIcons.TintLight, EditorIcons.Home);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Startup scene");
                ImGui.SameLine();
            }
            else {
                ImGui.Dummy(new SysVec2(ImGui.CalcTextSize(EditorIcons.Home).X, 0));
                ImGui.SameLine();
            }

            ImGui.TextDisabled($"{i}");
            ImGui.SameLine();
            ImGui.TextUnformatted(SceneName(scenes[i]));
            ImGui.SameLine();
            ImGui.TextDisabled($"  {scenes[i]}");

            // Row actions, right-aligned: up / down / remove (same idiom as InspectorPanel's row eye).
            ImGui.SameLine();
            float bw = EditorIcons.SmallButtonWidth(EditorIcons.ChevronDown);
            float gap = ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bw * 3 - gap * 2);
            ImGui.BeginDisabled(i == 0);
            if (EditorIcons.GhostButtonSmall("up", EditorIcons.ChevronRight, "Move up")) { moveFrom = i; moveTo = i - 1; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i == scenes.Count - 1);
            if (EditorIcons.GhostButtonSmall("down", EditorIcons.ChevronDown, "Move down")) { moveFrom = i; moveTo = i + 1; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (EditorIcons.GhostButtonSmall("rm", EditorIcons.Delete, "Remove")) remove = i;

            ImGui.PopID();
        }
        ImGui.EndChild();

        if (remove >= 0) scenes.RemoveAt(remove);
        if (moveFrom >= 0 && moveTo >= 0 && moveTo < scenes.Count)
            (scenes[moveFrom], scenes[moveTo]) = (scenes[moveTo], scenes[moveFrom]);

        if (ImGui.Button($"{EditorIcons.Add}  Add Open Scene")) AddOpenScene();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add the currently loaded scene");
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Folder}  Add Scene...")) ImGui.OpenPopup("##addscene");
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Save}  Save List")) SaveManifestScenes();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Write the list into project.json without building");

        DrawAddScenePopup(scale);
    }

    void DrawAddScenePopup(float scale) {
        ImGui.SetNextWindowSize(new SysVec2(360 * scale, 360 * scale), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##addscene"))
            return;

        ImGui.TextDisabled("Add a scene to the build");
        ImGui.Separator();
        ImGui.BeginChild("##scenelist");
        foreach (string path in AllScenePaths()) {
            bool already = scenes.Contains(path, StringComparer.OrdinalIgnoreCase);
            ImGui.BeginDisabled(already);
            var (icon, tint) = EditorIcons.ForAssetExtension(".scene");
            ImGui.TextColored(tint, icon);
            ImGui.SameLine();
            if (ImGui.Selectable($"{SceneName(path)}##{path}", false)) {
                scenes.Add(path);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            ImGui.TextDisabled(already ? "  (in build)" : $"  {path}");
            ImGui.EndDisabled();
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    // ---- Output + build button ----------------------------------------------

    void DrawOutputSection(float scale) {
        ImGui.TextDisabled("Output Folder");

        // Field + Browse button on one row: shrink the field to leave room for the button.
        float browseW = ImGui.CalcTextSize($"{EditorIcons.Folder}  Browse...").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - browseW - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputText("##out", ref outputDir, 512);
        ImGui.SameLine();
        ImGui.BeginDisabled(building);
        if (ImGui.Button($"{EditorIcons.Folder}  Browse..."))
            BrowseForOutput();
        ImGui.EndDisabled();

        ImGui.Checkbox("Self-contained (bundle .NET 9 — target needs no runtime installed)", ref selfContained);
    }

    void BrowseForOutput() {
        // Seed the dialog at the current path (or its nearest existing parent, or the project root).
        string seed = outputDir;
        while (!string.IsNullOrEmpty(seed) && !Directory.Exists(seed))
            seed = Path.GetDirectoryName(seed);
        if (string.IsNullOrEmpty(seed))
            seed = project.RootPath;

        string picked = NativeDialogs.PickFolder("Select build output folder", seed);
        if (!string.IsNullOrEmpty(picked))
            outputDir = picked;
    }

    void DrawBuildButton(float scale) {
        bool canBuild = !building && scenes.Count > 0 && !string.IsNullOrWhiteSpace(outputDir);

        ImGui.BeginDisabled(!canBuild);
        if (ImGui.Button(building ? "Building..." : $"{EditorIcons.Package}  Build",
                         new SysVec2(160 * scale, 32 * scale)))
            StartBuild();
        ImGui.EndDisabled();

        if (scenes.Count == 0) {
            ImGui.SameLine();
            ImGui.TextColored(EditorIcons.TintLight, "Add at least one scene to build.");
        }
        else if (lastSucceeded == true) {
            ImGui.SameLine();
            if (ImGui.Button($"{EditorIcons.FolderOpen}  Open Folder", new SysVec2(0, 32 * scale)))
                OpenOutputFolder();
        }
    }

    void DrawLog(float scale) {
        ImGui.Dummy(new SysVec2(0, 6 * scale));
        ImGui.Separator();

        bool? ok;
        string summary;
        string[] lines;
        lock (gate) {
            ok = lastSucceeded;
            summary = lastSummary;
            lines = log.ToArray();
        }

        if (summary is not null) {
            SysVec4 color = ok == true ? new SysVec4(0.5f, 0.85f, 0.5f, 1f)
                          : ok == false ? new SysVec4(0.9f, 0.45f, 0.45f, 1f)
                          : EditorIcons.TintGeneric;
            ImGui.TextColored(color, summary);
        }

        ImGui.BeginChild("##buildlog", new SysVec2(0, 0), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar);
        foreach (string line in lines)
            ImGui.TextUnformatted(line);
        if (building && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4)
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    // ---- actions ------------------------------------------------------------

    void StartBuild() {
        var options = new BuildPipeline.Options {
            Project = project,
            OutputDir = outputDir.Trim(),
            ScenesInBuild = scenes.ToList(),
            SelfContained = selfContained,
        };

        // Persist the list immediately so it survives even if the build later fails.
        SaveManifestScenes();

        lock (gate) {
            log.Clear();
            building = true;
            lastSucceeded = null;
            lastSummary = "Building...";
        }
        BuildProgress.Begin();   // drives the full-window BusyOverlay card

        var thread = new Thread(() => {
            BuildPipeline.Result result = BuildPipeline.Build(options, Append);
            lock (gate) {
                building = false;
                lastSucceeded = result.Success;
                lastSummary = result.Success
                    ? $"{EditorIcons.Check}  Build succeeded."
                    : $"{EditorIcons.Error}  Build failed: {result.Error}";
                if (!result.Success && result.Error is not null)
                    log.Add(result.Error);
            }
            BuildProgress.End();
        }) { IsBackground = true, Name = "BallisticBuild" };
        thread.Start();
    }

    // Each pipeline message is appended to the in-window log. Top-level phase headlines (the pipeline
    // emits one per phase, in order) advance the overlay's determinate bar; indented sub-messages
    // ("  copying Assets...") only update the card's detail line so the bar doesn't race ahead.
    void Append(string message) {
        lock (gate) log.Add(message);

        if (message.StartsWith(' '))
            BuildProgress.Detail = message.Trim();
        else
            BuildProgress.Step(message);
    }

    void AddOpenScene() {
        // The loaded scene's source path isn't tracked on the Scene object; offer the manifest's
        // current startup scene as the closest "open scene" proxy, else nudge the user to the picker.
        var current = scenes.Count == 0 ? project.Manifest.StartupScene : null;
        if (!string.IsNullOrEmpty(current) && !scenes.Contains(current, StringComparer.OrdinalIgnoreCase))
            scenes.Add(current);
        else if (scenes.Count > 0 || string.IsNullOrEmpty(current))
            ImGui.OpenPopup("##addscene");
    }

    void SaveManifestScenes() {
        project.Manifest.ScenesInBuild = scenes.ToList();
        project.Manifest.StartupScene = scenes.FirstOrDefault();
        PipelineJson.Write(Path.Combine(project.RootPath, "project.json"), project.Manifest);
        Append($"Saved {scenes.Count} scene(s) to project.json.");
    }

    void OpenOutputFolder() {
        if (Directory.Exists(outputDir))
            Process.Start("explorer.exe", $"\"{outputDir}\"");
    }

    // ---- helpers ------------------------------------------------------------

    IEnumerable<string> AllScenePaths() =>
        AssetDatabase.EnumerateAssets()
            .Select(kv => kv.Key)
            .Where(p => p.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    static string SceneName(string assetPath) => Path.GetFileNameWithoutExtension(assetPath);

    static string Sanitize(string name) {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Game" : name;
    }
}
