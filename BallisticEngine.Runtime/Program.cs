using BallisticEngine;
using BallisticEngine.AssetPipeline;

internal class Program {
    public static void Main(string[] args) {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();

        var shipped = ShippedProjectPath();
        bool playerMode = shipped is not null || args.Contains("--player");

        var projectPath = shipped
                          ?? (positional.Length > 0 ? Path.GetFullPath(positional[0]) : DefaultProjectPath());

        if (args.Contains("--author-scene")) {
            SceneAuthoring.AuthorMainScene(projectPath);
            return;
        }

        PlayerSettings player = ReadPlayerSettings(projectPath);
        WindowMode mode = player.WindowMode;
        if (args.Contains("--fullscreen")) mode = WindowMode.Fullscreen;
        if (args.Contains("--windowed")) mode = WindowMode.Windowed;
        if (shipped is null && !args.Contains("--fullscreen") && mode == WindowMode.Fullscreen)
            mode = WindowMode.Windowed;

        BallisticEngine.Profiling.TracyProfiler.TryInstall("Ballistic Runtime");

        bool headlessMode = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT") is not null
                            || Environment.GetEnvironmentVariable("BALLISTIC_QUERY") is not null
                            || Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_MOTION_DUMP") is not null
                            || Environment.GetEnvironmentVariable("BALLISTIC_DX12_FPSBENCH") is not null
                            || Environment.GetEnvironmentVariable("BALLISTIC_DX12_RESIZE_STRESS") == "1";
        int resW = player.Width, resH = player.Height;
        if (Environment.GetEnvironmentVariable("BALLISTIC_RES") is { } resEnv) {
            var parts = resEnv.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int rw) && int.TryParse(parts[1], out int rh)
                && rw > 0 && rh > 0) { resW = rw; resH = rh; }
        }

        IBallisticEngineRuntime runtime;
        if (headlessMode) {
            Console.WriteLine($"[Backend] DX12 host (headless — screenshot/query path) {resW}x{resH}.");
            runtime = new Dx12HeadlessRuntime(resW, resH);
        }
        else {
            Console.WriteLine("[Backend] DX12 host (windowed player).");
            runtime = new Dx12WindowedRuntime(resW, resH,
                fullscreen: mode == WindowMode.Fullscreen,
                borderless: mode == WindowMode.Borderless,
                title: player.ProductName);
        }
        BEngineEntry engineEntry = new(runtime, projectPath, playerMode);
        engineEntry.Run();

        JobSystem.Shutdown();

        Audio.Shutdown();
    }

    static PlayerSettings ReadPlayerSettings(string projectPath) {
        try {
            var manifestPath = Path.Combine(projectPath, "project.json");
            if (File.Exists(manifestPath)) {
                ProjectManifest manifest = PipelineJson.Read<ProjectManifest>(manifestPath);
                if (manifest is not null)
                    return PlayerSettings.OrDefault(manifest);
            }
        }
        catch {
        }
        return PlayerSettings.OrDefault(new ProjectManifest());
    }

    static string ShippedProjectPath() {
        foreach (var name in new[] { "Data", "Project" }) {
            var candidate = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(Path.Combine(candidate, "project.json")))
                return candidate;
        }
        return null;
    }

    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
