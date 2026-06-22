using System.Diagnostics;

namespace BallisticEngine.AssetPipeline;

public static class BuildPipeline {
    public sealed class Options {
        public required BallisticProject Project { get; init; }
        public required string OutputDir { get; init; }
        public IReadOnlyList<string> ScenesInBuild { get; init; } = [];
        public string Configuration { get; init; } = "Release";
        public string RuntimeIdentifier { get; init; } = "win-x64";
        public bool SelfContained { get; init; } = true;

        public PlayerSettings Player { get; init; }
    }

    public sealed record Result(bool Success, string OutputDir, string ExePath, long TotalBytes, string Error = null);

    static readonly HashSet<string> ShippedAssetExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".scene", ".mat", ".volume", ".shader", ".glsl", ".cubemap", ".prefab", ".asset",
    };

    public static Result Build(Options options, Action<string> log = null) {
        log ??= _ => { };
        var project = options.Project;
        PlayerSettings player = options.Player ?? PlayerSettings.OrDefault(project.Manifest);
        string productName = string.IsNullOrWhiteSpace(player.ProductName) ? project.Manifest.Name : player.ProductName;

        try {
            log("Compiling game scripts...");
            if (!GameScripts.TryCompile(project, out _, out _)) {
                return Fail(options, "Game scripts failed to compile — fix the errors in the Console and rebuild.");
            }

            log("Baking assets + guid map...");
            AssetDatabase.Refresh();
            AssetDatabase.WriteGuidMap();

            log("Writing build settings into project.json...");
            WriteManifestScenes(project, options.ScenesInBuild);

            var runtimeCsproj = LocateRuntimeCsproj();
            if (runtimeCsproj is null) {
                return Fail(options, "Could not locate BallisticEngine.Runtime.csproj to publish " +
                                     "(is this a source checkout? builds require the engine source).");
            }

            Directory.CreateDirectory(options.OutputDir);
            log($"Publishing player ({options.Configuration}, {options.RuntimeIdentifier}, single-file)...");
            if (!Publish(runtimeCsproj, options, player, productName, project, log, out var publishError)) {
                return Fail(options, publishError);
            }

            var exe = RenameExe(options.OutputDir, productName, log);

            log("Copying game data...");
            CopyGameData(project, options.OutputDir, log);

            long size = DirectorySize(options.OutputDir);
            log($"Build complete: {options.OutputDir}  ({Megabytes(size)} MB)");
            return new Result(true, options.OutputDir, exe, size);
        }
        catch (Exception e) {
            return Fail(options, e.Message);
        }
    }

    static Result Fail(Options options, string error) =>
        new(false, options.OutputDir, null, 0, error);

    static void WriteManifestScenes(BallisticProject project, IReadOnlyList<string> scenes) {
        var manifest = project.Manifest;
        manifest.ScenesInBuild = scenes.Where(s => !string.IsNullOrEmpty(s)).ToList();
        manifest.StartupScene = manifest.ScenesInBuild.FirstOrDefault();
        PipelineJson.Write(Path.Combine(project.RootPath, "project.json"), manifest);
    }

    static bool Publish(string runtimeCsproj, Options options, PlayerSettings player, string productName,
        BallisticProject project, Action<string> log, out string error) {
        error = null;

        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(runtimeCsproj);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(options.Configuration);
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(options.RuntimeIdentifier);
        startInfo.ArgumentList.Add(options.SelfContained ? "--self-contained" : "--no-self-contained");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(options.OutputDir);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:m");
        startInfo.ArgumentList.Add("-p:PublishSingleFile=true");
        startInfo.ArgumentList.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
        startInfo.ArgumentList.Add("-p:DebugType=none");
        startInfo.ArgumentList.Add("-p:DebugSymbols=false");
        startInfo.ArgumentList.Add("-p:ShipBuild=true");

        startInfo.ArgumentList.Add($"-p:AssemblyName={Sanitize(productName)}");
        startInfo.ArgumentList.Add($"-p:Product={Escape(productName)}");
        if (!string.IsNullOrWhiteSpace(player.CompanyName))
            startInfo.ArgumentList.Add($"-p:Company={Escape(player.CompanyName)}");
        if (TryNormalizeVersion(player.Version, out var version)) {
            startInfo.ArgumentList.Add($"-p:Version={version}");
            startInfo.ArgumentList.Add($"-p:FileVersion={version}");
            startInfo.ArgumentList.Add($"-p:InformationalVersion={Escape(player.Version)}");
        }

        var iconAbsolute = ResolveIcon(project, player, log);
        if (iconAbsolute is not null)
            startInfo.ArgumentList.Add($"-p:ApplicationIcon={iconAbsolute}");

        string output;
        int exitCode;
        try {
            using Process process = Process.Start(startInfo);
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(600_000)) {
                process.Kill(entireProcessTree: true);
                error = "`dotnet publish` timed out after 10 minutes.";
                return false;
            }
            exitCode = process.ExitCode;
        }
        catch (Exception e) {
            error = $"failed to run `dotnet publish`: {e.Message}";
            return false;
        }

        if (exitCode != 0) {
            error = "`dotnet publish` failed. Output tail:\n" +
                    string.Join('\n', output.Split('\n').TakeLast(20));
            return false;
        }

        return true;
    }

    static string RenameExe(string outputDir, string gameName, Action<string> log) {
        var target = Path.Combine(outputDir, Sanitize(gameName) + ".exe");

        foreach (var pdb in new[] { "BallisticEngine.Runtime.pdb", Sanitize(gameName) + ".pdb" }) {
            var p = Path.Combine(outputDir, pdb);
            if (File.Exists(p)) { try { File.Delete(p); } catch {
            } }
        }

        if (File.Exists(target))
            return target;

        var published = Path.Combine(outputDir, "BallisticEngine.Runtime.exe");
        if (!File.Exists(published))
            return target;

        try {
            File.Move(published, target);
            return target;
        }
        catch (Exception e) {
            log($"  (kept default exe name — rename failed: {e.Message})");
            return published;
        }
    }

    static string Escape(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    static bool TryNormalizeVersion(string raw, out string normalized) {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var trimmed = raw.Trim().TrimStart('v', 'V');
        var parts = trimmed.Split('.');
        if (parts.Length is < 1 or > 4)
            return false;
        var nums = new int[Math.Max(3, parts.Length)];
        for (int i = 0; i < parts.Length; i++) {
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0)
                return false;
        }
        normalized = string.Join('.', nums);
        return true;
    }

    static string ResolveIcon(BallisticProject project, PlayerSettings player, Action<string> log) {
        if (string.IsNullOrWhiteSpace(player.IconPath))
            return null;

        var abs = Path.IsPathRooted(player.IconPath)
            ? player.IconPath
            : Path.Combine(project.RootPath, player.IconPath);

        if (!File.Exists(abs)) {
            log($"  (icon not found, using default: {player.IconPath})");
            return null;
        }
        if (!abs.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) {
            log($"  (icon must be a .ico, using default: {player.IconPath})");
            return null;
        }
        return abs;
    }

    static string LocateRuntimeCsproj() {
        const string relative = @"BallisticEngine.Runtime\BallisticEngine.Runtime.csproj";

        var root = Environment.GetEnvironmentVariable("BALLISTIC_ENGINE_ROOT");
        if (!string.IsNullOrEmpty(root)) {
            var explicitPath = Path.Combine(root, relative);
            if (File.Exists(explicitPath))
                return explicitPath;
        }

        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(BuildPipeline).Assembly.Location)!);
        for (var d = dir; d is not null; d = d.Parent) {
            var candidate = Path.Combine(d.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    static void CopyGameData(BallisticProject project, string outputDir, Action<string> log) {
        var dest = Path.Combine(outputDir, "Data");
        Directory.CreateDirectory(dest);

        File.Copy(Path.Combine(project.RootPath, "project.json"),
                  Path.Combine(dest, "project.json"), overwrite: true);

        var items = new List<(string LogicalPath, string SourceFile)>();
        int dataAssets = 0, excludedSources = 0;

        if (Directory.Exists(project.AssetsPath)) {
            foreach (var file in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories)) {
                var ext = Path.GetExtension(file);
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                    !ShippedAssetExtensions.Contains(ext)) {
                    excludedSources++;
                    continue;
                }
                items.Add((ToLogical(project.RootPath, file), file));
                dataAssets++;
            }
        }

        var artifactsDir = Path.Combine(project.LibraryPath, "Artifacts");
        if (Directory.Exists(artifactsDir))
            foreach (var file in Directory.EnumerateFiles(artifactsDir, "*", SearchOption.AllDirectories))
                items.Add((ToLogical(project.RootPath, file), file));

        log($"  packing {items.Count} entries ({dataAssets} data assets, {excludedSources} sources excluded)...");
        ContentPack.Write(Path.Combine(dest, "content.pak"), items);

        var libDest = Path.Combine(dest, "Library");
        CopyFileIfExists(project.ArtifactDatabasePath, Path.Combine(libDest, "ArtifactDB.json"));
        CopyFileIfExists(Path.Combine(project.LibraryPath, GuidMap.FileName),
                         Path.Combine(libDest, GuidMap.FileName));
        foreach (var sub in new[] { "ScriptAssemblies", "ProbeVolumes", "ReflectionProbes" }) {
            var src = Path.Combine(project.LibraryPath, sub);
            if (Directory.Exists(src))
                CopyDirectory(src, Path.Combine(libDest, sub), IsScriptDebris);
        }
    }

    static string ToLogical(string rootPath, string absoluteFile) =>
        Path.GetRelativePath(rootPath, absoluteFile).Replace('\\', '/');

    static bool IsScriptDebris(string file) =>
        file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".sources", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);

    static void CopyDirectory(string source, string dest, Func<string, bool> skip = null) {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
            if (skip is not null && skip(file))
                continue;
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }

    static void CopyFileIfExists(string source, string dest) {
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
    }

    static long DirectorySize(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

    static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1");

    static string Sanitize(string name) {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "Game" : name;
    }
}
