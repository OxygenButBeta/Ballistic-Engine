namespace BallisticEngine.AssetPipeline;

// The player's content layer: a stack of mounted .pak archives that the asset reads consult before
// touching the filesystem. The editor/dev runtime mounts nothing (HasAny == false) and everything
// falls through to loose files exactly as before — so this is invisible until a build mounts a pack.
//
// Mount order = override order: a path present in a LATER-mounted pack wins, which is the whole point
// for the future — a patch / DLC / streamed-level pack mounted on top transparently replaces base
// content without rebuilding the exe. Logical paths are forward-slash, relative to the project root
// (e.g. "Library/Artifacts/<guid>.bmesh", "Assets/Levels/Main.scene").
public static class ContentMount {
    // Last mounted = highest priority, so iterate in reverse for lookups.
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

    // True if any mounted pack has this entry (later mounts shadow earlier ones, but for existence
    // any match counts).
    public static bool Contains(string logicalPath) {
        lock (gate) {
            for (int i = packs.Count - 1; i >= 0; i--)
                if (packs[i].Contains(logicalPath))
                    return true;
        }
        return false;
    }

    // Reads an entry from the highest-priority pack that has it. False if no mounted pack does.
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
