using BallisticEngine.Cli.Commands;

namespace BallisticEngine.Cli;

// `bal` — the engine's command-line surface (AI-operability roadmap layer 1). Every verb prints JSON
// to stdout and returns an honest exit code: 0 = success, 1 = a handled error (printed as a JSON
// {"error": ...} object), 2 = usage error (unknown verb / bad args). Diagnostics go to stderr so
// stdout stays machine-parseable.
//
// Build order (per the roadmap): schema -> validate -> scene CRUD -> import -> map/describe. This is
// the foundation; verbs are added as separate ICommand implementations with zero central plumbing.
internal static class Program {
    static readonly IReadOnlyDictionary<string, ICommand> Commands = new ICommand[] {
        new SchemaCommand(),
        new ValidateCommand(),
        new DescribeCommand(),
    }.ToDictionary(c => c.Name, StringComparer.Ordinal);

    static int Main(string[] args) {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        string verb = args[0];
        if (!Commands.TryGetValue(verb, out ICommand? command)) {
            Console.Error.WriteLine($"bal: unknown command '{verb}'. Run 'bal --help' for the verb list.");
            return 2;
        }

        try {
            return command.Run(args[1..]);
        }
        catch (CliUsageException usage) {
            Console.Error.WriteLine($"bal {verb}: {usage.Message}");
            Console.Error.WriteLine(command.Usage);
            return 2;
        }
        catch (Exception ex) {
            // A handled failure: emit the error as JSON on stdout (so a caller parsing stdout sees it)
            // and a human line on stderr.
            Json.WriteError(ex.Message);
            Console.Error.WriteLine($"bal {verb}: {ex.Message}");
            return 1;
        }
    }

    static void PrintUsage() {
        Console.Error.WriteLine("bal — Ballistic Engine command-line interface");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: bal <command> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Commands:");
        foreach (ICommand c in Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            Console.Error.WriteLine($"  {c.Name,-12}{c.Summary}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Every command prints JSON to stdout. Exit codes: 0 ok, 1 error, 2 usage.");
    }
}
