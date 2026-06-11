using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline;

public readonly record struct ScriptDiagnostic(string File, int Line, int Column, bool IsError, string Code, string Message) {
    public override string ToString() => $"{File}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}

// Project C# scripting (Unity-style): every .cs under Assets\ compiles into one game assembly
// (Library\ScriptAssemblies\GameScripts.dll) via `dotnet build`, then loads into a collectible
// AssemblyLoadContext so the editor can rebuild + reload without restarting. Engine/OpenTK types
// resolve from the default context (never re-loaded here, or type identity would split), and the
// dll is loaded from BYTES so the file stays unlocked for the next build.
//
// Scripts.csproj is generated at the project ROOT (so IDEs open the game code as a real project
// with engine references) and stays engine-managed while its marker comment is present — delete
// the marker to take ownership (NuGet packages, compiler settings); the engine then builds the
// file as-is and never rewrites it.
public static class GameScripts {
    public const string AssemblyName = "GameScripts";

    static GameScriptLoadContext loadContext;

    public static Assembly LoadedAssembly { get; private set; }

    // Diagnostics from the most recent TryCompile, newest build only (for tooling/CLI surfaces).
    public static IReadOnlyList<ScriptDiagnostic> LastDiagnostics { get; private set; } = [];

    // True while the LATEST compile attempt failed — Unity's "fix compile errors" state: the
    // bootstrap wires SceneManager.PlayBlocked to this so play mode (editor) and the player
    // refuse to run a project whose game assembly doesn't build. Cleared by the next good
    // compile (including the up-to-date fast path) or when the project has no scripts.
    public static bool CompileFailed { get; private set; }

    // Compile + load in one go (engine bootstrap). Returns null when the project has no scripts
    // or the build failed — the engine keeps running either way; errors are in the log.
    public static Assembly CompileAndLoad(BallisticProject project) {
        if (!TryCompile(project, out var assemblyPath) || assemblyPath is null)
            return null;

        return LoadFrom(assemblyPath);
    }

    // Builds the script assembly if any sources changed. Returns false on compiler errors
    // (assemblyPath stays null); true with a null assemblyPath when the project has no scripts.
    public static bool TryCompile(BallisticProject project, out string assemblyPath) =>
        TryCompile(project, out assemblyPath, out _);

    // `rebuilt` reports whether the compiler actually ran — false on the up-to-date fast path,
    // which lets the editor's focus-regain check skip the scene-reload dance when nothing changed.
    public static bool TryCompile(BallisticProject project, out string assemblyPath, out bool rebuilt) {
        assemblyPath = null;
        rebuilt = false;

        List<string> sources = FindSources(project);
        if (sources.Count == 0) {
            CompileFailed = false;
            return true;
        }

        var csprojPath = EnsureProjectFile(project);
        var dllPath = Path.Combine(project.LibraryPath, "ScriptAssemblies", AssemblyName + ".dll");

        if (IsUpToDate(csprojPath, sources, dllPath)) {
            CompileFailed = false;
            assemblyPath = dllPath;
            return true;
        }

        if (!RunBuild(project, csprojPath)) {
            CompileFailed = true;
            return false;
        }

        CompileFailed = false;
        File.WriteAllText(StampPath(dllPath), StampContent(project, sources));
        assemblyPath = dllPath;
        rebuilt = true;
        return true;
    }

    // Loads the built assembly into a fresh collectible context. Call Unload first when replacing
    // an already-loaded one (the editor's rebuild flow does: compile -> unload -> load).
    public static Assembly LoadFrom(string assemblyPath) {
        if (!File.Exists(assemblyPath)) {
            Debugging.LogError($"Game scripts: built assembly not found at '{assemblyPath}'.");
            return null;
        }

        loadContext = new GameScriptLoadContext(Path.GetDirectoryName(assemblyPath));
        LoadedAssembly = loadContext.LoadFromBytes(assemblyPath);

        var componentNames = LoadedAssembly.GetTypes()
            .Where(t => !t.IsAbstract && (typeof(Behaviour).IsAssignableFrom(t) ||
                                          typeof(SceneBehaviour).IsAssignableFrom(t) ||
                                          typeof(VolumeComponent).IsAssignableFrom(t)))
            .Select(t => t.Name)
            .ToList();
        Debugging.Log($"Game scripts: loaded {Path.GetFileName(assemblyPath)} " +
                      $"({componentNames.Count} components: {string.Join(", ", componentNames)})");
        return LoadedAssembly;
    }

    // Unloads the current script assembly context. Collection is deferred until the GC sees no
    // remaining references to its types — the caller must have cleared the scene, the component
    // registry, and the volume stack first, or the old assembly lingers (logged, not fatal).
    public static void Unload() {
        if (loadContext is null)
            return;

        loadContext.Unload();
        loadContext = null;
        LoadedAssembly = null;
    }

    static List<string> FindSources(BallisticProject project) {
        if (!Directory.Exists(project.AssetsPath))
            return [];

        return Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    const string GeneratedMarker = "Generated by Ballistic Engine";

    // Returns the project's Scripts.csproj path, (re)generating it when engine-managed.
    // While the marker comment is in the file the engine keeps it up to date (rewritten only
    // when the content actually changes, so its mtime stays meaningful for the up-to-date
    // check); a file without the marker is user-owned and never touched. Public because the
    // editor also opens this file in the IDE ("Edit Script" / "Open C# Project").
    public static string EnsureProjectFile(BallisticProject project) {
        var csproj = Path.Combine(project.RootPath, "Scripts.csproj");
        var content = GeneratedProjectContent();

        if (!File.Exists(csproj)) {
            File.WriteAllText(csproj, content);
        }
        else {
            var existing = File.ReadAllText(csproj);
            if (existing.Contains(GeneratedMarker) && existing != content)
                File.WriteAllText(csproj, content);
        }

        // The generated csproj used to live in Library\ScriptProject\ — drop the stale copy.
        var legacyDir = Path.Combine(project.LibraryPath, "ScriptProject");
        if (Directory.Exists(legacyDir)) {
            try { Directory.Delete(legacyDir, recursive: true); }
            catch { /* locked by an IDE — harmless leftover */ }
        }

        return csproj;
    }

    // Engine binaries directory of the RUNNING host — referenced by the script project so game
    // code compiles against exactly the engine it will run inside.
    static string EngineBinariesDir() =>
        Path.GetDirectoryName(typeof(GameScripts).Assembly.Location);

    static string GeneratedProjectContent() {
        var engineDir = EngineBinariesDir();
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <!-- Generated by Ballistic Engine: compiles Assets\**\*.cs into Library\ScriptAssemblies\GameScripts.dll.
                   The engine rewrites this file while this marker comment is present. To customize it
                   (NuGet packages, compiler settings), DELETE this comment - the engine then builds
                   the file as-is and never touches it again. -->

              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>disable</Nullable>
                <AssemblyName>{AssemblyName}</AssemblyName>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
                <OutputPath>Library\ScriptAssemblies\</OutputPath>
                <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
                <DebugType>portable</DebugType>
                <!-- Passed by the engine at build time; the literal path is an IDE fallback. -->
                <BallisticEngineDir Condition="'$(BallisticEngineDir)' == ''">{engineDir}</BallisticEngineDir>
              </PropertyGroup>

              <ItemGroup>
                <Compile Include="Assets\**\*.cs" />
              </ItemGroup>

              <ItemGroup>
                <Reference Include="BallisticEngine">
                  <HintPath>$(BallisticEngineDir)\BallisticEngine.dll</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="OpenTK.Mathematics">
                  <HintPath>$(BallisticEngineDir)\OpenTK.Mathematics.dll</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="OpenTK.Windowing.GraphicsLibraryFramework">
                  <HintPath>$(BallisticEngineDir)\OpenTK.Windowing.GraphicsLibraryFramework.dll</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="OpenTK.Windowing.Common">
                  <HintPath>$(BallisticEngineDir)\OpenTK.Windowing.Common.dll</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>

            </Project>
            """;
    }

    static string StampPath(string dllPath) => dllPath + ".sources";

    // The source SET is stamped alongside the dll: mtime checks alone miss deleted files.
    static string StampContent(BallisticProject project, List<string> sources) =>
        string.Join('\n', sources.Select(project.ToAssetPath));

    static bool IsUpToDate(string csprojPath, List<string> sources, string dllPath) {
        if (!File.Exists(dllPath) || !File.Exists(StampPath(dllPath)))
            return false;

        DateTime built = File.GetLastWriteTimeUtc(dllPath);
        if (File.GetLastWriteTimeUtc(csprojPath) > built)
            return false;
        if (sources.Any(s => File.GetLastWriteTimeUtc(s) > built))
            return false;

        // Compare the stamped source set (paths are project-relative, see StampContent).
        return StampMatches(File.ReadAllText(StampPath(dllPath)), sources);
    }

    static bool StampMatches(string stamped, List<string> sources) {
        var stampedSet = stamped.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (stampedSet.Length != sources.Count)
            return false;

        // Stamped paths are project-relative ("Assets/..."), sources absolute; compare by suffix.
        for (var i = 0; i < sources.Count; i++) {
            var normalized = sources[i].Replace(Path.DirectorySeparatorChar, '/');
            if (!normalized.EndsWith(stampedSet[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // ---- dotnet build -------------------------------------------------------

    static readonly Regex DiagnosticPattern = new(
        @"^\s*(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<msg>.+?)(\s+\[[^\]]+\])?$",
        RegexOptions.Compiled);

    static bool RunBuild(BallisticProject project, string csprojPath) {
        var stopwatch = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.RootPath,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(csprojPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:m");
        startInfo.ArgumentList.Add($"-p:BallisticEngineDir={EngineBinariesDir()}");

        string output;
        int exitCode;
        try {
            using Process process = Process.Start(startInfo);
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000)) {
                process.Kill(entireProcessTree: true);
                Debugging.LogError("Game scripts: `dotnet build` timed out after 120s.");
                return false;
            }
            exitCode = process.ExitCode;
        }
        catch (Exception e) {
            Debugging.LogError($"Game scripts: failed to run `dotnet build`: {e.Message}");
            return false;
        }

        List<ScriptDiagnostic> diagnostics = ParseDiagnostics(project, output);
        LastDiagnostics = diagnostics;

        foreach (ScriptDiagnostic d in diagnostics) {
            if (d.IsError)
                Debugging.LogError($"Game scripts: {d}");
            else
                Debugging.LogWarning($"Game scripts: {d}");
        }

        var errors = diagnostics.Count(d => d.IsError);
        var warnings = diagnostics.Count - errors;
        if (exitCode != 0) {
            // Surface SOMETHING when the build fails without parseable Cxxxx lines (bad csproj,
            // missing SDK, ...) — the raw tail is better than a silent count of zero.
            if (errors == 0)
                Debugging.LogError($"Game scripts: build failed (exit {exitCode}). Output tail:\n" +
                                   string.Join('\n', output.Split('\n').TakeLast(15)));
            else
                Debugging.LogError($"Game scripts: build FAILED — {errors} error(s), {warnings} warning(s).");
            return false;
        }

        Debugging.Log($"Game scripts: build succeeded in {stopwatch.ElapsedMilliseconds} ms" +
                      (warnings > 0 ? $" ({warnings} warning(s))" : "") + ".");
        return true;
    }

    static List<ScriptDiagnostic> ParseDiagnostics(BallisticProject project, string output) {
        List<ScriptDiagnostic> diagnostics = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n')) {
            Match match = DiagnosticPattern.Match(line.TrimEnd('\r'));
            if (!match.Success)
                continue;

            var file = match.Groups["file"].Value.Trim();
            // Project-relative paths read better in the console and are stable across machines.
            if (Path.IsPathRooted(file) && file.StartsWith(project.RootPath, StringComparison.OrdinalIgnoreCase))
                file = project.ToAssetPath(file);

            var diagnostic = new ScriptDiagnostic(
                file,
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["col"].Value),
                match.Groups["sev"].Value == "error",
                match.Groups["code"].Value,
                match.Groups["msg"].Value.Trim());

            // MSBuild repeats each diagnostic in its summary section; keep one.
            if (seen.Add(diagnostic.ToString()))
                diagnostics.Add(diagnostic);
        }

        return diagnostics;
    }

    // ---- Load context -------------------------------------------------------

    sealed class GameScriptLoadContext : AssemblyLoadContext {
        readonly string assembliesDir;

        public GameScriptLoadContext(string assembliesDir) : base(AssemblyName, isCollectible: true) {
            this.assembliesDir = assembliesDir;
        }

        // Engine, OpenTK, and BCL assemblies must come from the default context (returning null
        // falls back to it) — loading a second copy here would split type identity and break
        // every `is Behaviour` check. Only sibling dlls in ScriptAssemblies (a user csproj's
        // package dependencies) load into this context.
        protected override Assembly Load(AssemblyName name) {
            var candidate = Path.Combine(assembliesDir, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromBytes(candidate) : null;
        }

        // Byte-loading keeps the dll file unlocked so the next `dotnet build` can overwrite it
        // while this assembly is still loaded (Windows locks memory-mapped assemblies).
        public Assembly LoadFromBytes(string path) {
            using var dll = new MemoryStream(File.ReadAllBytes(path));
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdbPath))
                return LoadFromStream(dll);

            using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
            return LoadFromStream(dll, pdb);
        }
    }
}
