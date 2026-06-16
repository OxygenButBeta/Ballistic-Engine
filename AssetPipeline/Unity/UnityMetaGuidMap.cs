namespace BallisticEngine.AssetPipeline.Unity;

// Builds a map from Unity's asset GUIDs (the 32-hex ids in .meta files, which Unity scenes/prefabs
// reference) to the actual files on disk. After extracting a .unitypackage, every asset has its
// original "<file>.meta" beside it carrying "guid: <hex>". Unity scene refs use those GUIDs, so this
// is how a {fileID, guid} reference resolves to a real model/material/texture file.
//
// Reads Unity .meta YAML directly (NOT the engine's JSON .meta): a Unity meta is YAML with a top-level
// "guid: <32hex>" line. We scan the given roots for *.meta and index by that guid.
public static class UnityMetaGuidMap {
    // Returns guid (32-hex) -> absolute path of the asset the meta describes (the meta path minus the
    // ".meta" suffix).
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
                    map.TryAdd(guid, assetPath); // first wins on the rare duplicate guid
                }
            }
            catch (Exception exception) {
                Debugging.LogWarning($"Unity meta scan of '{root}' failed: {exception.Message}");
            }
        }

        return map;
    }

    // Pulls "guid: <hex>" from a Unity .meta. Returns null if absent (e.g. an engine JSON .meta, whose
    // guid is a JSON field, not a leading YAML line).
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
            // unreadable meta -> skip it
        }
        return null;
    }

    public static bool IsHexGuid(string s) => s is { Length: 32 } && s.All(char.IsAsciiHexDigit);
}
