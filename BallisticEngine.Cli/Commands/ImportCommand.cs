using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Cli.Commands;

// `bal import <project>` — headless, idempotent asset import: the engine's own Refresh pipeline
// without an editor or window. Walks Assets\, mints .meta sidecars for new files, (re)imports
// sources whose content/settings/importer version changed, writes Library artifacts. A second run
// with no changes reports everything up-to-date and is near-instant — agents run this after adding
// files externally, before loading scenes. Exit 0 = clean (even if nothing to do); exit 1 = any
// asset FAILED to import (each failure is logged with its path on stderr).
internal sealed class ImportCommand : ICommand {
    public string Name => "import";
    public string Summary => "Import a project's assets headlessly (idempotent).";
    public string Usage =>
        """
        Usage: bal import <project-dir-or-any-path-inside> [--force] [--quiet]
          --force  rebuild every Library artifact from source (Reimport All)
          --quiet  suppress per-file progress and info logs on stderr
        """;

    public int Run(string[] args) {
        string? pathArg = null;
        bool force = false, quiet = false;
        foreach (string a in args) {
            switch (a) {
                case "--force": force = true; break;
                case "--quiet": quiet = true; break;
                default:
                    if (pathArg is null) pathArg = a;
                    else throw new CliUsageException($"unexpected argument '{a}'");
                    break;
            }
        }
        if (pathArg is null) throw new CliUsageException("expected a project path");

        // Accept the project root or any path inside it (a scene path, Assets\, ...).
        string root = SceneFile.ResolveProjectRoot(pathArg);

        // Mirror engine logs to stderr so import failures are visible (stdout stays JSON-clean).
        // Warnings/errors always; info only when not --quiet.
        Debugging.OnMessage += (message, level) => {
            if (level > 0 || !quiet) Console.Error.WriteLine(message);
        };

        BallisticProject project = BallisticProject.Open(root);
        AssetDatabase.Initialize(project);
        if (!quiet)
            AssetDatabase.ImportProgress = label => Console.Error.WriteLine($"  importing {label}");

        RefreshResult result = AssetDatabase.Refresh(forceAll: force);

        Json.Write(new {
            ok = result.Failed == 0,
            scanned = result.Scanned,
            imported = result.Imported,
            upToDate = result.UpToDate,
            failed = result.Failed,
            elapsedMs = result.ElapsedMs,
        });
        return result.Failed == 0 ? 0 : 1;
    }
}
