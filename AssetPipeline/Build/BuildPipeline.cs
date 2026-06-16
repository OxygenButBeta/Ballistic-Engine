using System.Diagnostics;

namespace BallisticEngine.AssetPipeline;

// Produces a shippable standalone player from a project (the editor's Build window calls this).
//
// Layout of a finished build (outputDir) — deliberately CLEAN and source-free:
//   <Game>\
//     <Game>.exe          single self-contained file: .NET runtime + every engine/native dll bundled.
//     Data\
//       project.json      (with the build's ScenesInBuild written in)
//       Assets\           ONLY text/data assets: .scene/.mat/.volume/.shader/.glsl/.cubemap.
//                         Source models/textures/scripts (.fbx/.png/.cs/.pyscene) and .meta are
//                         NOT shipped — they stay private and the folder stays clean.
//       Library\
//         guidmap.json    baked path->guid table (lets the player resolve refs without metas)
//         ArtifactDB.json guid -> baked-artifact map
//         Artifacts\      the BINARY assets the player actually loads (.bmesh/.btex)
//         ScriptAssemblies\  pre-built GameScripts.dll (no SDK / no .cs needed at runtime)
//         ProbeVolumes\, ReflectionProbes\  baked GI/reflection (if present)
//
// The runtime finds Data\ next to the exe and boots in player mode (EngineBootstrap.PlayerMode):
// pre-baked Library, no `dotnet build`, no asset Refresh, fullscreen. Progress via the log callback.
public static class BuildPipeline {
    public sealed class Options {
        public required BallisticProject Project { get; init; }
        public required string OutputDir { get; init; }       // <Game> folder; created if missing.
        public IReadOnlyList<string> ScenesInBuild { get; init; } = [];  // ordered; [0] = startup.
        public string Configuration { get; init; } = "Release";
        public string RuntimeIdentifier { get; init; } = "win-x64";
        public bool SelfContained { get; init; } = true;       // bundle .NET so the target needs no runtime.

        // Player identity baked into the published exe. Null → derived from the project manifest.
        public PlayerSettings Player { get; init; }
    }

    public sealed record Result(bool Success, string OutputDir, string ExePath, long TotalBytes, string Error = null);

    // Text/data asset extensions the player reads directly at runtime — these MUST ship. Everything
    // else under Assets\ is source (imported into binary artifacts at build time) and is dropped.
    static readonly HashSet<string> ShippedAssetExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".scene", ".mat", ".volume", ".shader", ".glsl", ".cubemap", ".prefab", ".asset",
    };

    // Runs the whole build synchronously. Call from a worker thread (it shells out to `dotnet publish`
    // and copies hundreds of MB). `log` is invoked from the calling thread with human-readable steps.
    public static Result Build(Options options, Action<string> log = null) {
        log ??= _ => { };
        var project = options.Project;
        PlayerSettings player = options.Player ?? PlayerSettings.OrDefault(project.Manifest);
        string productName = string.IsNullOrWhiteSpace(player.ProductName) ? project.Manifest.Name : player.ProductName;

        try {
            // 1. Game scripts: make sure GameScripts.dll is current (the player loads it without an SDK).
            log("Compiling game scripts...");
            if (!GameScripts.TryCompile(project, out _, out _)) {
                return Fail(options, "Game scripts failed to compile — fix the errors in the Console and rebuild.");
            }

            // 2. Refresh assets so every artifact is baked and the GUID maps are complete, then bake
            //    the path->guid table the source-free player resolves references from.
            log("Baking assets + guid map...");
            AssetDatabase.Refresh();
            AssetDatabase.WriteGuidMap();

            // 3. Persist the scene list into the manifest so the shipped player reads it back.
            log("Writing build settings into project.json...");
            WriteManifestScenes(project, options.ScenesInBuild);

            // 4. Publish the player exe (single self-contained file → clean folder, no .NET install).
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

            // 5. Rename the published exe to the product name (root shows just <Product>.exe).
            var exe = RenameExe(options.OutputDir, productName, log);

            // 6. Copy the trimmed, source-free content into <Game>\Data.
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

    // ---- manifest -----------------------------------------------------------

    static void WriteManifestScenes(BallisticProject project, IReadOnlyList<string> scenes) {
        var manifest = project.Manifest;
        manifest.ScenesInBuild = scenes.Where(s => !string.IsNullOrEmpty(s)).ToList();
        // Keep the legacy field pointed at the startup scene for older readers.
        manifest.StartupScene = manifest.ScenesInBuild.FirstOrDefault();
        PipelineJson.Write(Path.Combine(project.RootPath, "project.json"), manifest);
    }

    // ---- dotnet publish -----------------------------------------------------

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
        // Single self-contained file: one <Game>.exe in the root, every managed + native dll bundled
        // inside (self-extracted to a per-user temp dir on first launch). Streaming / RT content updates
        // are unaffected — they live in Data\ packs (ContentPack), independent of the exe. Strip pdbs.
        startInfo.ArgumentList.Add("-p:PublishSingleFile=true");
        startInfo.ArgumentList.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
        startInfo.ArgumentList.Add("-p:DebugType=none");
        startInfo.ArgumentList.Add("-p:DebugSymbols=false");
        // ShipBuild=true: the Runtime csproj switches to WinExe (GUI subsystem) so NO console window
        // pops up for the shipped game. Scoped inside that csproj (not -p:OutputType, which would leak
        // onto the engine library and break it — it has no Main). Dev `dotnet run` stays a console Exe.
        startInfo.ArgumentList.Add("-p:ShipBuild=true");

        // --- player identity: baked into the exe's file metadata (Details tab) + AssemblyName so the
        //     published exe is already named <Product>.exe before the rename step. ---
        startInfo.ArgumentList.Add($"-p:AssemblyName={Sanitize(productName)}");
        startInfo.ArgumentList.Add($"-p:Product={Escape(productName)}");
        if (!string.IsNullOrWhiteSpace(player.CompanyName))
            startInfo.ArgumentList.Add($"-p:Company={Escape(player.CompanyName)}");
        if (TryNormalizeVersion(player.Version, out var version)) {
            startInfo.ArgumentList.Add($"-p:Version={version}");
            startInfo.ArgumentList.Add($"-p:FileVersion={version}");
            startInfo.ArgumentList.Add($"-p:InformationalVersion={Escape(player.Version)}");
        }

        // --- icon: embed the .ico into the exe so it has a custom taskbar/file icon. The project-
        //     relative path is resolved to absolute; a missing/invalid icon is skipped with a log
        //     line rather than failing the whole build. ---
        var iconAbsolute = ResolveIcon(project, player, log);
        if (iconAbsolute is not null)
            startInfo.ArgumentList.Add($"-p:ApplicationIcon={iconAbsolute}");

        string output;
        int exitCode;
        try {
            using Process process = Process.Start(startInfo);
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(600_000)) {  // 10 min — a self-contained publish restores + builds a lot.
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

    // Ensures the root shows <Product>.exe. With -p:AssemblyName set, publish already emits
    // <Product>.exe, so this is usually a no-op; the BallisticEngine.Runtime.exe fallback covers a
    // publish that ignored the rename (or an empty/invalid product name). Drops stray .pdb either way.
    static string RenameExe(string outputDir, string gameName, Action<string> log) {
        var target = Path.Combine(outputDir, Sanitize(gameName) + ".exe");

        foreach (var pdb in new[] { "BallisticEngine.Runtime.pdb", Sanitize(gameName) + ".pdb" }) {
            var p = Path.Combine(outputDir, pdb);
            if (File.Exists(p)) { try { File.Delete(p); } catch { /* harmless */ } }
        }

        // Already published under the product name (the AssemblyName path) — done.
        if (File.Exists(target))
            return target;

        var published = Path.Combine(outputDir, "BallisticEngine.Runtime.exe");
        if (!File.Exists(published))
            return target; // nothing to rename; caller still reports the expected path

        try {
            File.Move(published, target);
            return target;
        }
        catch (Exception e) {
            log($"  (kept default exe name — rename failed: {e.Message})");
            return published;
        }
    }

    // MSBuild Product/Company values are passed as one ArgumentList entry, so spaces are fine (no shell
    // quoting); we only guard against a stray newline that would corrupt the arg.
    static string Escape(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    // dotnet's Version property wants a numeric "x.y.z[.w]". Accept the user's free-form string only if
    // it normalizes to that; otherwise skip the version props (InformationalVersion still carries the raw
    // string). "1.0" -> "1.0.0", "v1.2.3" -> "1.2.3", "beta" -> false.
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

    // Resolves the player's icon to an absolute .ico path, or null (engine default) when unset/invalid.
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

    // The Runtime csproj lives in the engine SOURCE tree. The editor runs from <repo>\BallisticEngine.Editor\
    // bin\<cfg>\net9.0\, so walk up from the engine assembly until we find the Runtime project. An explicit
    // BALLISTIC_ENGINE_ROOT override (repo root) wins — for hosts that run from outside the source tree.
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

    // ---- content copy -------------------------------------------------------

    // Builds <Game>\Data:
    //   project.json                  loose — the player reads the manifest before mounting anything.
    //   content.pak                   the bulky, hideable content: every BINARY artifact (.bmesh/.btex)
    //                                  + the text/data assets (scenes/materials/volumes/shaders/cubemaps),
    //                                  keyed by logical path. Sources (.fbx/.png/.cs) + .meta NEVER added.
    //   Library\                      small bootstrap metadata kept LOOSE (read before/around the mount):
    //                                  guidmap.json, ArtifactDB.json, ScriptAssemblies\GameScripts.dll,
    //                                  ProbeVolumes\, ReflectionProbes\. None of these expose source.
    // content.pak is the single mountable archive future patch/DLC/streamed packs sit on top of.
    static void CopyGameData(BallisticProject project, string outputDir, Action<string> log) {
        var dest = Path.Combine(outputDir, "Data");
        Directory.CreateDirectory(dest);

        File.Copy(Path.Combine(project.RootPath, "project.json"),
                  Path.Combine(dest, "project.json"), overwrite: true);

        // --- pack: text/data assets + binary artifacts ---
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

        // --- loose Library metadata (read by the bootstrap, around the mount) ---
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

    // Absolute file under the project root -> forward-slash logical path ("Assets/...", "Library/...").
    static string ToLogical(string rootPath, string absoluteFile) =>
        Path.GetRelativePath(rootPath, absoluteFile).Replace('\\', '/');

    // ScriptAssemblies ships GameScripts.dll, but not its debug/build debris.
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
