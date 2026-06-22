namespace BallisticEngine.AssetPipeline.Unity;

public static class UnityMetaGuidMap {
    public static Dictionary<string, string> Build(IEnumerable<string> rootDirectories) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootDirectories) {
            if (!Directory.Exists(root))
                continue;
            try {
                foreach (var metaPath in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)) {
                    var guid = ReadGuid(metaPath);
                    if (guid is null)
                        continue;
                    var assetPath = metaPath[..^".meta".Length];
                    map.TryAdd(guid, assetPath);
                }
            }
            catch (Exception exception) {
                Debugging.LogWarning($"Unity meta scan of '{root}' failed: {exception.Message}");
            }
        }

        return map;
    }

    static string ReadGuid(string metaPath) {
        try {
            foreach (var line in File.ReadLines(metaPath)) {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("guid:", StringComparison.Ordinal))
                    continue;
                var value = trimmed["guid:".Length..].Trim().Trim('"', '\'');
                return IsHexGuid(value) ? value : null;
            }
        }
        catch {
        }
        return null;
    }

    public static bool IsHexGuid(string s) => s is { Length: 32 } && s.All(char.IsAsciiHexDigit);
}
