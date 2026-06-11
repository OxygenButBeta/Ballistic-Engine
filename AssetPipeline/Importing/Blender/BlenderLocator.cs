namespace BallisticEngine.AssetPipeline;

// Finds the Blender executable used to read .blend files (Assimp can't extract cameras/lights
// from .blend and breaks on modern Blender versions, so the importer drives Blender's own Python
// to export glTF + a scene sidecar). Resolution order, first hit wins:
//   1. BLENDER_PATH env var (absolute path to blender.exe / blender) — explicit override.
//   2. Standard install locations (Program Files\Blender Foundation\Blender X.Y\ on Windows;
//      common /Applications and /usr paths elsewhere), newest version first.
//   3. "blender" on PATH.
// Returns null when none is found; the importer then logs a one-line hint and skips the asset.
public static class BlenderLocator {
    // Cache the resolved path for the lifetime of the process — assets import in parallel and a
    // big project may hold dozens of .blend files; we don't want to re-scan Program Files each time.
    static string cached;
    static bool resolved;

    public static string Find() {
        if (resolved)
            return cached;

        cached = Resolve();
        resolved = true;
        return cached;
    }

    static string Resolve() {
        var fromEnv = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        foreach (var candidate in InstallCandidates())
            if (File.Exists(candidate))
                return candidate;

        // Last resort: rely on PATH. We can't File.Exists this, so hand back the bare command
        // and let the process launch fail loudly if it isn't actually on PATH.
        return OnPath("blender") ?? OnPath("blender.exe");
    }

    static IEnumerable<string> InstallCandidates() {
        if (OperatingSystem.IsWindows()) {
            // Newest "Blender X.Y" folder first so a machine with several versions uses the latest.
            foreach (var programFiles in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }) {
                var foundation = Path.Combine(programFiles, "Blender Foundation");
                if (!Directory.Exists(foundation))
                    continue;

                foreach (var versionDir in Directory.EnumerateDirectories(foundation)
                             .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    yield return Path.Combine(versionDir, "blender.exe");
            }
        }
        else if (OperatingSystem.IsMacOS()) {
            yield return "/Applications/Blender.app/Contents/MacOS/Blender";
        }
        else {
            yield return "/usr/bin/blender";
            yield return "/usr/local/bin/blender";
            yield return "/snap/bin/blender";
        }
    }

    static string OnPath(string command) {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator)) {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try {
                var full = Path.Combine(dir.Trim(), command);
                if (File.Exists(full))
                    return full;
            }
            catch {
                // Malformed PATH entry — skip it.
            }
        }
        return null;
    }
}
