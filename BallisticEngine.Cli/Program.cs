using BallisticEngine.Cli.Commands;

namespace BallisticEngine.Cli;

internal static class Program {
    static readonly IReadOnlyDictionary<string, ICommand> Commands = new ICommand[] {
        new SchemaCommand(),
        new RemoteSchemaCommand(),
        new ValidateCommand(),
        new DescribeCommand(),
        new SceneCommand(),
        new ImportCommand(),
        new AssetsCommand(),
        new MapCommand(),
        new SimulateCommand(),
        new ImgDiffCommand(),
        new RenderCommand(),
        new QueryCommand(),
        new GBufferCommand(),
        new PerfCommand(),
        new AgentsCommand(),
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
