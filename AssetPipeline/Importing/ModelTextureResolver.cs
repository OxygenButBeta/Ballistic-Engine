using System.Text.Json.Nodes;
using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine.AssetPipeline;

sealed class ModelTextureResolver {
    readonly string modelDir;
    readonly string projectRoot;
    readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> fileIndex;

    public ModelTextureResolver(string modelDir, string projectRoot) {
        this.modelDir = modelDir;
        this.projectRoot = projectRoot;
    }

    public string Resolve(string rawPath) {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        if (cache.TryGetValue(rawPath, out var cached))
            return cached;

        var resolved = ResolveUncached(rawPath.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar));
        cache[rawPath] = resolved;
        return resolved;
    }

    string ResolveUncached(string raw) {
        var fileName = Path.GetFileName(raw);

        string[] candidates = [
            Path.Combine(modelDir, raw),
            Path.IsPathRooted(raw) ? raw : null,
            Path.Combine(modelDir, fileName),
            Path.Combine(modelDir, "Textures", fileName),
            Path.Combine(modelDir, "textures", fileName),
        ];

        foreach (var candidate in candidates) {
            if (candidate is not null && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        BuildFileIndex();
        return fileIndex.GetValueOrDefault(fileName);
    }

    void BuildFileIndex() {
        if (fileIndex is not null)
            return;

        fileIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IndexTree(modelDir);

        var parent = Path.GetDirectoryName(modelDir);
        if (parent is not null && parent.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            IndexTree(parent);
    }

    void IndexTree(string root) {
        try {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                fileIndex.TryAdd(Path.GetFileName(file), file);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Texture search under '{root}' failed: {exception.Message}");
        }
    }
}
