using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Cli.Commands;

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

        string root = SceneFile.ResolveProjectRoot(pathArg);

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
