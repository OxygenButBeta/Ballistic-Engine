namespace BallisticEngine.Tests.Reflection;

// Architecture guard (the user's CRITICAL rule): ImGui may be used INSIDE the editor, but the PLAYER
// (Runtime) and every non-editor layer must NEVER reference it — a leak would pull the ImGui binding into
// the shipped game. This scans the source tree (not a built assembly, so it catches a `using` the moment
// it's typed) and asserts:
//
//   1. ONLY BallisticEngine.Editor.csproj references the Hexa.NET.ImGui PackageReference.
//   2. NO .cs file outside BallisticEngine.Editor\ contains `using Hexa.NET.ImGui` / a `Hexa.NET.ImGui.`
//      qualified reference (game-editor scripts under SampleProject\Assets\Editor\ are intentionally
//      EXEMPT — they compile into the editor-only EditorScripts.dll, never the player).
//
// Source-scan rationale: a reflection check over loaded assemblies can't see a dependency that doesn't
// happen to load in the harness, and can't catch a `using` in a file that didn't compile. Scanning text
// is the cheapest reliable boundary check and matches the "auditable by grep" layering contract in
// CLAUDE.md. The repo root is found by walking up to the directory holding BallisticEngine.slnx.
internal static class ImGuiBoundaryTests {
    const string ImGuiNamespace = "Hexa.NET.ImGui";
    const string EditorProjectDir = "BallisticEngine.Editor";

    public static int Run() {
        var h = new Harness();

        string repoRoot = FindRepoRoot();
        if (repoRoot is null) {
            // Can't locate the source tree (e.g. running from a packaged drop) — don't fail the suite,
            // but make the skip visible so it isn't a silent pass.
            Console.WriteLine("[ImGuiBoundary] SKIP — repo root (BallisticEngine.slnx) not found from " +
                              AppContext.BaseDirectory);
            return 0;
        }

        // 1) Only the editor csproj may carry the ImGui PackageReference.
        var offendingCsprojs = new List<string>();
        foreach (string csproj in Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)) {
            if (IsUnderGeneratedOrBin(repoRoot, csproj)) continue;
            if (IsEditorProject(csproj)) continue;
            string text = ReadSafe(csproj);
            if (text.Contains(ImGuiNamespace, StringComparison.Ordinal))
                offendingCsprojs.Add(Rel(repoRoot, csproj));
        }
        h.Check("Only BallisticEngine.Editor.csproj references Hexa.NET.ImGui",
            offendingCsprojs.Count == 0,
            offendingCsprojs.Count == 0 ? "" : "leaked into: " + string.Join(", ", offendingCsprojs));

        // 2) No .cs file outside the editor project (and outside Assets\Editor\ game-editor scripts) may
        //    import or qualify ImGui.
        var offendingSources = new List<string>();
        foreach (string cs in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)) {
            if (IsUnderGeneratedOrBin(repoRoot, cs)) continue;
            if (IsUnderEditorProject(repoRoot, cs)) continue;
            if (IsUnderEditorScripts(cs)) continue;   // Assets\Editor\ -> editor-only EditorScripts.dll
            // This guard file itself NAMES the namespace (as a string constant) without using it — exempt it.
            if (Path.GetFileName(cs).Equals("ImGuiBoundaryTests.cs", StringComparison.OrdinalIgnoreCase)) continue;
            string text = ReadSafe(cs);
            if (text.Contains("using " + ImGuiNamespace, StringComparison.Ordinal) ||
                text.Contains(ImGuiNamespace + ".", StringComparison.Ordinal))
                offendingSources.Add(Rel(repoRoot, cs));
        }
        h.Check("No .cs outside BallisticEngine.Editor references Hexa.NET.ImGui (player never sees ImGui)",
            offendingSources.Count == 0,
            offendingSources.Count == 0 ? "" : "leaked in: " + string.Join(", ", offendingSources));

        return h.Report("ImGuiBoundary");
    }

    // Walk up from the harness's base directory to the repo root (the dir containing BallisticEngine.slnx).
    static string? FindRepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            if (File.Exists(Path.Combine(dir.FullName, "BallisticEngine.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    static bool IsEditorProject(string csproj) =>
        Path.GetFileName(csproj).Equals(EditorProjectDir + ".csproj", StringComparison.OrdinalIgnoreCase);

    static bool IsUnderEditorProject(string repoRoot, string path) {
        string editorDir = Path.Combine(repoRoot, EditorProjectDir) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(editorDir, StringComparison.OrdinalIgnoreCase);
    }

    // Game-editor scripts live under a project's Assets\Editor\ and compile into the editor-only
    // EditorScripts.dll (referencing the editor) — they are ALLOWED the editor API but in practice use the
    // IEditorGui seam, not ImGui. They never enter the player, so exempt them from the boundary.
    static bool IsUnderEditorScripts(string path) {
        string p = Path.GetFullPath(path).Replace('\\', '/');
        return p.Contains("/Assets/Editor/", StringComparison.OrdinalIgnoreCase);
    }

    // Skip build output and the generated obj/bin trees (compiled copies, NuGet stubs) — only source counts.
    static bool IsUnderGeneratedOrBin(string repoRoot, string path) {
        string p = Path.GetFullPath(path).Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/.git/", StringComparison.Ordinal);
    }

    static string ReadSafe(string path) {
        try { return File.ReadAllText(path); }
        catch { return ""; }
    }

    static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
