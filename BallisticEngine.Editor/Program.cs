using BallisticEngine;
using BallisticEngine.Editor;

internal class Program {
    public static void Main(string[] args) {
        var projectPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : DefaultProjectPath();

        // Load persisted editor settings before the UI/theme is built so the saved accent applies.
        EditorPrefs.Load();

        BallisticEngine.Profiling.TracyProfiler.TryInstall("Ballistic Editor");

        GLBallisticEngineWindow window = new(1600, 900);
        _ = new EditorApplication(window, projectPath);
        window.Run();

        // JobSystem workers are foreground threads; without this the process never exits.
        JobSystem.Shutdown();
    }

    // BallisticEngine.Editor\bin\Debug\net9.0 -> repo root -> SampleProject
    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
