namespace BallisticEngine.AssetPipeline;

public sealed class BallisticProject {
    public string RootPath { get; }
    public string AssetsPath { get; }
    public string LibraryPath { get; }
    public string ArtifactsPath { get; }
    public string ArtifactDatabasePath { get; }
    public ProjectManifest Manifest { get; }

    BallisticProject(string rootPath, ProjectManifest manifest) {
        RootPath = rootPath;
        Manifest = manifest;
        AssetsPath = Path.Combine(rootPath, "Assets");
        LibraryPath = Path.Combine(rootPath, "Library");
        ArtifactsPath = Path.Combine(LibraryPath, "Artifacts");
        ArtifactDatabasePath = Path.Combine(LibraryPath, "ArtifactDB.json");
    }

    public static BallisticProject Open(string rootPath) {
        rootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Project directory not found: '{rootPath}'");

        bool hasAssets = Directory.Exists(Path.Combine(rootPath, "Assets"));
        bool isPacked = File.Exists(Path.Combine(rootPath, "content.pak"));
        if (!hasAssets && !isPacked)
            throw new DirectoryNotFoundException(
                $"'{rootPath}' is not a Ballistic project: it has no Assets directory or content.pak.");

        var manifestPath = Path.Combine(rootPath, "project.json");
        ProjectManifest manifest;
        if (File.Exists(manifestPath)) {
            manifest = PipelineJson.Read<ProjectManifest>(manifestPath);
        }
        else {
            manifest = new ProjectManifest { Name = new DirectoryInfo(rootPath).Name };
            PipelineJson.Write(manifestPath, manifest);
            Debugging.LogWarning($"project.json not found; created a default one at '{manifestPath}'.");
        }

        BallisticProject project = new(rootPath, manifest);
        if (hasAssets)
            Directory.CreateDirectory(project.ArtifactsPath);
        return project;
    }

    public string ResolveAbsolute(string assetPath) =>
        Path.GetFullPath(Path.Combine(RootPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));

    public string ToAssetPath(string absolutePath) =>
        Path.GetRelativePath(RootPath, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
