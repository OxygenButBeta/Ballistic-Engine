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

        string? repoRoot = FindRepoRoot();
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

        // 3) RATCHET (Phase-9 strict, in progress): inside the editor, panel/window BODIES should draw
        //    through the IEditorGui seam, not raw ImGui. The EditorWindow-framework migration removes ImGui
        //    from Panels\ + Windows\ one file at a time. This check freezes the CURRENT set: every Panels\ or
        //    Windows\ .cs that still imports ImGui must be on PendingImGuiMigration, and the list must have no
        //    STALE entries (a file that no longer imports ImGui must be dropped). So a NEW leak fails, a
        //    REGRESSION that re-adds ImGui to a migrated panel fails, and finishing a migration without
        //    pruning the list fails — the only green path is forward. When the list empties, this becomes the
        //    absolute "no ImGui in Panels/Windows" guard. Seam adapters that legitimately keep style/ScalarField
        //    raw-ImGui (the plan's pragmatic boundary) stay on the list permanently; everything else burns down.
        string panelsDir = Path.Combine(repoRoot, EditorProjectDir, "Panels") + Path.DirectorySeparatorChar;
        string windowsDir = Path.Combine(repoRoot, EditorProjectDir, "Windows") + Path.DirectorySeparatorChar;
        var importsImGui = new List<string>();
        foreach (string cs in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)) {
            if (IsUnderGeneratedOrBin(repoRoot, cs)) continue;
            string full = Path.GetFullPath(cs);
            bool underPanels = full.StartsWith(panelsDir, StringComparison.OrdinalIgnoreCase);
            bool underWindows = full.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);
            if (!underPanels && !underWindows) continue;
            // A real `using` line — not a comment that merely names the namespace (the example/test files).
            if (HasImGuiUsing(ReadSafe(cs)))
                importsImGui.Add(Rel(repoRoot, cs));
        }
        var allow = new HashSet<string>(PendingImGuiMigration, StringComparer.OrdinalIgnoreCase);
        var unexpected = importsImGui.Where(f => !allow.Contains(f)).ToList();
        var stale = PendingImGuiMigration.Where(f => !importsImGui.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        h.Check("No NEW raw-ImGui leak into Panels/Windows (migrate through IEditorGui)",
            unexpected.Count == 0,
            unexpected.Count == 0 ? "" : "not on the allowlist: " + string.Join(", ", unexpected));
        h.Check("No STALE Panels/Windows allowlist entries (prune as panels migrate)",
            stale.Count == 0,
            stale.Count == 0 ? "" : "migrated — remove from PendingImGuiMigration: " + string.Join(", ", stale));

        return h.Report("ImGuiBoundary");
    }

    // The Panels\/Windows\ .cs files that STILL draw with raw ImGui, pending their EditorWindow-framework
    // migration. The ratchet above forbids any file NOT on this list from importing ImGui, and forbids stale
    // entries — so the list can only shrink. Paths are repo-relative with forward slashes.
    // (Standalone windows are all migrated; Windows\ carries none here.)
    static readonly string[] PendingImGuiMigration = {
        // Inspector — the big one (Phase 7): the panel shell + its adapters/preview/layout subfiles. Several
        // adapters legitimately keep style/ScalarField raw-ImGui (the plan's pragmatic boundary) and stay.
        "BallisticEngine.Editor/Panels/InspectorPanel.cs",
        "BallisticEngine.Editor/Panels/Inspector/Adapters/ScalarField.cs",
        // (ImGuiVolumeGui + ImGuiComponentGui migrated — route through EditorGui.Shared; dropped from list.)
        "BallisticEngine.Editor/Panels/Inspector/Preview/ComponentPreviews.cs",
        // (InspectorLayout migrated — its 2 draw helpers route through EditorGui.Shared; rest is arithmetic.)
        "BallisticEngine.Editor/Panels/Inspector/AssetInspectors/AssetInspectors.cs",
        // Hierarchy + Assets (Phase 5): drag-drop heavy, needs seam drag-drop/context-menu first.
        "BallisticEngine.Editor/Panels/HierarchyPanel.cs",
        "BallisticEngine.Editor/Panels/AssetBrowserPanel.cs",
        // (ConsolePanel migrated — now draws through the seam's style scope; dropped from the list.)
        // Inline inspector drawers (Phase 7): VolumeProfile + BEvent — custom-header ImGui, plan-exempt.
        "BallisticEngine.Editor/Panels/VolumeProfileEditor.cs",
        "BallisticEngine.Editor/Panels/BEventEditor.cs",
        // Viewport overlay — bespoke window flags + pivot positioning; doesn't fit the dockable model (exempt).
        "BallisticEngine.Editor/Panels/StatsPanel.cs",
    };

    // True if the text has a real `using Hexa.NET.ImGui;` directive (start of a line, ignoring leading space),
    // not just a comment mentioning the namespace.
    static bool HasImGuiUsing(string text) {
        foreach (string raw in text.Split('\n')) {
            string line = raw.TrimStart();
            if (line.StartsWith("using " + ImGuiNamespace, StringComparison.Ordinal) &&
                (line.Contains(";", StringComparison.Ordinal)))
                return true;
        }
        return false;
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
