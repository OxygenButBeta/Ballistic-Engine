namespace BallisticEngine.AssetPipeline;

public static class ContentMount {
    static readonly List<ContentPack> packs = new();
    static readonly object gate = new();

    public static bool HasAny {
        get { lock (gate) return packs.Count > 0; }
    }

    public static void Mount(string packPath) {
        var pack = ContentPack.Open(packPath);
        lock (gate) packs.Add(pack);
        Debugging.Log($"Mounted content pack '{Path.GetFileName(packPath)}' ({pack.Entries.Count} entries).");
    }

    public static void UnmountAll() {
        lock (gate) {
            foreach (var pack in packs)
                pack.Dispose();
            packs.Clear();
        }
    }

    public static bool Contains(string logicalPath) {
        lock (gate) {
            for (int i = packs.Count - 1; i >= 0; i--)
                if (packs[i].Contains(logicalPath))
                    return true;
        }
        return false;
    }

    public static bool TryReadBytes(string logicalPath, out byte[] bytes) {
        lock (gate) {
            for (int i = packs.Count - 1; i >= 0; i--) {
                if (packs[i].Contains(logicalPath)) {
                    bytes = packs[i].Read(logicalPath);
                    return bytes is not null;
                }
            }
        }
        bytes = null;
        return false;
    }

    public static bool TryReadText(string logicalPath, out string text) {
        if (TryReadBytes(logicalPath, out var bytes)) {
            text = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        text = null;
        return false;
    }
}
