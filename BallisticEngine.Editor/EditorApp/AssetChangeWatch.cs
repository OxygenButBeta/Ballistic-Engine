namespace BallisticEngine.Editor;

// Detects EXTERNAL changes to Assets\ (IDE renames, Windows-Explorer copies/deletes) for the
// focus-regain refresh, Unity-style. A full AssetDatabase.Refresh on every alt-tab would flash
// the busy overlay even when nothing changed; this fingerprint scan (file paths + write times,
// order-independent) costs tens of milliseconds on thousands of files and has no side effects.
//
// The fingerprint is re-snapshotted after every completed refresh (AsyncAssetImport), so editor-
// initiated file writes (New Material, drop imports, importer-generated .mat siblings) don't
// read as "external" on the next focus.
internal static class AssetChangeWatch {
    static readonly object gate = new();
    static bool initialized;
    static ulong fingerprint;

    // True when the on-disk state no longer matches the last snapshot. Never true before the
    // first snapshot (startup import) — the initial import covers that window anyway.
    public static bool ChangedExternally() {
        lock (gate) {
            if (!initialized || AssetDatabase.Project is null)
                return false;
            return Compute() != fingerprint;
        }
    }

    // Called from the import worker right after AssetDatabase.Refresh, so the snapshot includes
    // files the refresh itself generated (model importers write sibling .mat sources).
    public static void Snapshot() {
        lock (gate) {
            if (AssetDatabase.Project is null)
                return;
            fingerprint = Compute();
            initialized = true;
        }
    }

    static ulong Compute() {
        var assetsPath = AssetDatabase.Project.AssetsPath;
        if (!Directory.Exists(assetsPath))
            return 0;

        ulong hash = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(assetsPath, "*", SearchOption.AllDirectories)) {
            // XOR-combine so enumeration order doesn't matter; mtime catches in-place edits,
            // the path catches renames/moves, the count catches pure deletions.
            hash ^= (ulong)HashCode.Combine(
                file.GetHashCode(StringComparison.OrdinalIgnoreCase),
                File.GetLastWriteTimeUtc(file).Ticks);
            count++;
        }

        return hash ^ ((ulong)count << 32);
    }
}
