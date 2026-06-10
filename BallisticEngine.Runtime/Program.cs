using BallisticEngine;

internal class Program {
    public static void Main(string[] args) {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var projectPath = positional.Length > 0
            ? Path.GetFullPath(positional[0])
            : DefaultProjectPath();

        // One-off: regenerate SampleProject's Main.scene, then exit.
        if (args.Contains("--author-scene")) {
            SceneAuthoring.AuthorMainScene(projectPath);
            return;
        }

        GLBallisticEngineWindow runtime = new(1280, 720);
        BEngineEntry engineEntry = new(runtime, projectPath);
        engineEntry.Run();
    }

    // BallisticEngine.Runtime\bin\Debug\net9.0 -> repo root -> SampleProject
    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
