using BallisticEngine;

internal class Program {
    // Entry point of ballistic engine
    public static void Main(string[] args) {
        var projectPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : DefaultProjectPath();

        GLBallisticEngineWindow runtime = new(1280, 720);
        BEngineEntry engineEntry = new(runtime, projectPath);
        engineEntry.Run();
    }

    // bin\Debug\net9.0 -> repo root -> SampleProject
    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SampleProject"));
}
