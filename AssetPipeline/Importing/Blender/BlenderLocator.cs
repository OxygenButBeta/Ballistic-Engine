namespace BallisticEngine.AssetPipeline;

public static class BlenderLocator {
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

        return OnPath("blender") ?? OnPath("blender.exe");
    }

    static IEnumerable<string> InstallCandidates() {
        if (OperatingSystem.IsWindows()) {
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
            }
        }
        return null;
    }
}
