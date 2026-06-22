using BallisticEngine.AssetPipeline;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

internal sealed class MapCommand : ICommand {
    public string Name => "map";
    public string Summary => "Project overview: scenes, scripts, asset inventory.";
    public string Usage => "Usage: bal map <project-dir-or-any-path-inside>";

    public int Run(string[] args) {
        if (args.Length != 1) throw new CliUsageException("expected a project path");
        string root = SceneFile.ResolveProjectRoot(args[0]);

        BallisticProject project = BallisticProject.Open(root);
        SceneFile.BuildRegistryForRoot(root);

        int totalAssets = 0;
        var byImporter = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byTopFolder = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scenePaths = new List<string>();
        foreach ((string path, MetaFile meta) in AssetsCommand.EnumerateMetas(root)) {
            totalAssets++;
            string importer = string.IsNullOrEmpty(meta.Importer) ? "(none)" : meta.Importer;
            byImporter[importer] = byImporter.GetValueOrDefault(importer) + 1;
            string[] parts = path.Split('/');
            string top = parts.Length > 2 ? $"Assets/{parts[1]}" : "Assets";
            byTopFolder[top] = byTopFolder.GetValueOrDefault(top) + 1;
            if (path.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
                scenePaths.Add(path);
        }

        string? startup = project.Manifest?.StartupScene?.Replace('\\', '/');
        var scenes = scenePaths.Select(path => {
            int entities = -1, components = -1;
            try {
                SceneDocument? doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(
                    File.ReadAllText(Path.Combine(root, path)));
                entities = doc?.Entities?.Count ?? 0;
                components = (doc?.Entities?.Sum(e => e.Components?.Count ?? 0) ?? 0)
                           + (doc?.SceneComponents?.Count ?? 0);
            }
            catch {
            }
            return new {
                path,
                startup = string.Equals(path, startup, StringComparison.OrdinalIgnoreCase) ? true : (bool?)null,
                entities,
                components,
            };
        }).ToList();

        var engineAssembly = typeof(SceneManager).Assembly;
        var scriptComponents = ComponentRegistry.Menu
            .Concat(ComponentRegistry.SceneMenu)
            .Where(e => e.Type.Assembly != engineAssembly)
            .Select(e => e.Type.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Json.Write(new {
            name = project.Manifest?.Name,
            root,
            startupScene = startup,
            scenes,
            scripts = new { components = scriptComponents },
            assets = new {
                total = totalAssets,
                byImporter,
                byTopFolder,
            },
        });
        return 0;
    }
}
