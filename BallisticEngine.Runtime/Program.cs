using BallisticEngine;
using BallisticEngine.AssetPipeline;

internal class Program {
    public static void Main(string[] args) {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();

        // A SHIPPED build drops the game's content in a "Data" folder next to the exe (see
        // BuildPipeline). When that exists it wins, and we run in player mode: pre-baked Library
        // artifacts, no `dotnet build`, no asset re-import. An explicit path arg or --player forces
        // player mode for testing a build folder from a dev checkout. --fullscreen / --windowed
        // override the project's saved window mode (debugging).
        var shipped = ShippedProjectPath();
        bool playerMode = shipped is not null || args.Contains("--player");

        var projectPath = shipped
                          ?? (positional.Length > 0 ? Path.GetFullPath(positional[0]) : DefaultProjectPath());

        // One-off: regenerate SampleProject's Main.scene, then exit.
        if (args.Contains("--author-scene")) {
            SceneAuthoring.AuthorMainScene(projectPath);
            return;
        }

        // Window identity + mode come from the project's PlayerSettings (Build panel). CLI flags win
        // over the saved mode so a dev can force windowed/fullscreen without editing project.json.
        PlayerSettings player = ReadPlayerSettings(projectPath);
        WindowMode mode = player.WindowMode;
        if (args.Contains("--fullscreen")) mode = WindowMode.Fullscreen;
        if (args.Contains("--windowed")) mode = WindowMode.Windowed;
        // A dev checkout (no shipped Data\) defaults to a window so it doesn't grab the whole screen
        // unless explicitly asked; a shipped build honours the saved mode.
        if (shipped is null && !args.Contains("--fullscreen") && mode == WindowMode.Fullscreen)
            mode = WindowMode.Windowed;

        BallisticEngine.Profiling.TracyProfiler.TryInstall("Ballistic Runtime");

        // Backend seam (DX12Migration.md): BALLISTIC_BACKEND=dx12 brings up the DX12 host instead of the
        // GL window. The DX12 host is currently HEADLESS (no swapchain) — it serves the screenshot
        // verification path; a windowed DX12 host (present + Windows input) comes later. GL is the default
        // host until the DX12 path reaches parity (then GL is deleted).
        IBallisticEngineRuntime runtime;
        if (RenderBackendSelector.Selected == RenderBackend.Dx12) {
            Console.WriteLine("[Backend] DX12 host (headless — screenshot path). " +
                              "Set BALLISTIC_SCREENSHOT to capture a frame.");
            runtime = new Dx12HeadlessRuntime(player.Width, player.Height);
        }
        else {
            runtime = new GLBallisticEngineWindow(player.Width, player.Height,
                fullscreen: mode == WindowMode.Fullscreen,
                borderless: mode == WindowMode.Borderless,
                title: player.ProductName);
        }
        BEngineEntry engineEntry = new(runtime, projectPath, playerMode);
        engineEntry.Run();

        // JobSystem workers are foreground threads; without this the process never exits.
        JobSystem.Shutdown();

        // Close the OpenAL device/context cleanly on shutdown.
        Audio.Shutdown();
    }

    // Reads the project's PlayerSettings (title/window mode/resolution) straight from project.json,
    // before the window is created. Falls back to defaults if the file is missing or malformed — the
    // window must come up regardless.
    static PlayerSettings ReadPlayerSettings(string projectPath) {
        try {
            var manifestPath = Path.Combine(projectPath, "project.json");
            if (File.Exists(manifestPath)) {
                ProjectManifest manifest = PipelineJson.Read<ProjectManifest>(manifestPath);
                if (manifest is not null)
                    return PlayerSettings.OrDefault(manifest);
            }
        }
        catch { /* fall through to defaults — never block startup on a settings read */ }
        return PlayerSettings.OrDefault(new ProjectManifest());
    }

    // A shipped game ships its content in "<exe dir>\Data" (project.json + assets + baked Library\).
    // Falls back to the legacy "Project" folder name. Returns the path when present, else null
    // (dev runs fall through to the SampleProject default).
    static string ShippedProjectPath() {
        foreach (var name in new[] { "Data", "Project" }) {
            var candidate = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(Path.Combine(candidate, "project.json")))
                return candidate;
        }
        return null;
    }

    // BallisticEngine.Runtime\bin\Debug\net9.0 -> repo root -> SampleProject
    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
