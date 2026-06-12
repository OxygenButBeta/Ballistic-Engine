using BallisticEngine;

internal class Program {
    public static void Main(string[] args) {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();

        // A SHIPPED build drops the game's content in a "Data" folder next to the exe (see
        // BuildPipeline). When that exists it wins, and we run in player mode: pre-baked Library
        // artifacts, no `dotnet build`, no asset re-import — and FULLSCREEN. An explicit path arg or
        // --player forces player mode for testing a build folder from a dev checkout (windowed unless
        // --fullscreen is also passed). --windowed forces a window even for a shipped build (debugging).
        var shipped = ShippedProjectPath();
        bool playerMode = shipped is not null || args.Contains("--player");
        bool fullscreen = (shipped is not null || args.Contains("--fullscreen")) && !args.Contains("--windowed");

        var projectPath = shipped
                          ?? (positional.Length > 0 ? Path.GetFullPath(positional[0]) : DefaultProjectPath());

        // One-off: regenerate SampleProject's Main.scene, then exit.
        if (args.Contains("--author-scene")) {
            SceneAuthoring.AuthorMainScene(projectPath);
            return;
        }

        BallisticEngine.Profiling.TracyProfiler.TryInstall("Ballistic Runtime");

        GLBallisticEngineWindow runtime = new(1280, 720, fullscreen);
        BEngineEntry engineEntry = new(runtime, projectPath, playerMode);
        engineEntry.Run();

        // JobSystem workers are foreground threads; without this the process never exits.
        JobSystem.Shutdown();

        // Close the OpenAL device/context cleanly on shutdown.
        Audio.Shutdown();
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
