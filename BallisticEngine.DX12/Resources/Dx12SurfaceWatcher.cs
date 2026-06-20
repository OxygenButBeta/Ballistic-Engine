using System;
using System.Collections.Generic;
using System.IO;

namespace BallisticEngine.DX12;

// Live file-watch for custom surface shaders. A FileSystemWatcher on the project Assets\ folder flags
// changed .surface/.hlsl files; the WATCHER THREAD only enqueues paths (no GPU work, no compile). The
// renderer drains the queue between frames (DrainPending) and recompiles on the main thread, which is
// where PSO creation is safe. Mirrors how the editor defers focus-regain script reloads off the OS
// callback — same discipline, but event-driven (no alt-tab needed) so a save updates the viewport live.
public sealed class Dx12SurfaceWatcher : IDisposable {
    FileSystemWatcher watcher;
    readonly object gate = new();
    readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase); // absolute paths changed since last drain
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
        lock (gate) pending.Add(e.FullPath);   // thread: just record — no compile, no GPU here
    }

    // Drain the changed-file set (called by the renderer between frames). Returns absolute paths to
    // recompile; empty when nothing changed. The caller maps each absolute path back to a project-
    // relative SourcePath and calls Dx12SurfaceShaderCache.Reload.
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
