namespace BallisticEngine.DX12;

public sealed class Dx12SurfaceWatcher : IDisposable {
    FileSystemWatcher watcher;
    readonly object gate = new();
    readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly string assetsRoot;

    public Dx12SurfaceWatcher(string assetsAbsolutePath) {
        assetsRoot = assetsAbsolutePath;
        if (string.IsNullOrEmpty(assetsRoot) || !Directory.Exists(assetsRoot))
            return;
        try {
            watcher = new FileSystemWatcher(assetsRoot) {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) {
            Debugging.LogWarning($"[surface] file watch unavailable: {e.Message}");
            watcher = null;
        }
    }

    static bool IsSurfaceSource(string path) {
        var ext = Path.GetExtension(path);
        return ext.Equals(".surface", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hlsl", StringComparison.OrdinalIgnoreCase);
    }

    void OnChanged(object sender, FileSystemEventArgs e) {
        if (!IsSurfaceSource(e.FullPath)) return;
        lock (gate) pending.Add(e.FullPath);
    }

    public bool HasPending { get { lock (gate) return pending.Count > 0; } }

    public List<string> DrainPending() {
        lock (gate) {
            if (pending.Count == 0) return null;
            var list = new List<string>(pending);
            pending.Clear();
            return list;
        }
    }

    public void Dispose() {
        if (watcher is not null) {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }
    }
}
