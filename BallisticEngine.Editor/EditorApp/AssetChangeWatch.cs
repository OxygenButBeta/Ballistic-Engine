namespace BallisticEngine.Editor;

internal static class AssetChangeWatch {
    static readonly object gate = new();
    static bool initialized;
    static ulong fingerprint;

    public static bool ChangedExternally() {
        lock (gate) {
            if (!initialized || AssetDatabase.Project is null)
                return false;
            return Compute() != fingerprint;
        }
    }

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
            hash ^= (ulong)HashCode.Combine(
                file.GetHashCode(StringComparison.OrdinalIgnoreCase),
                File.GetLastWriteTimeUtc(file).Ticks);
            count++;
        }

        return hash ^ ((ulong)count << 32);
    }
}
