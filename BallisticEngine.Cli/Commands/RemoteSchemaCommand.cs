namespace BallisticEngine.Cli.Commands;

internal sealed class RemoteSchemaCommand : ICommand {
    public string Name => "remote-schema";
    public string Summary => "Print the JSON catalog of editor command-port methods (pipe/MCP surface).";
    public string Usage =>
        "Usage: bal remote-schema [--method <name>]\n" +
        "  --method   only the named command-port method, e.g. 'component.set'";

    public int Run(string[] args) {
        string? onlyMethod = null;
        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--method": onlyMethod = Next(args, ref i, "--method"); break;
                default: throw new CliUsageException($"unexpected argument '{args[i]}'");
            }
        }

        RemoteSchema.CatalogEntry[] all = RemoteSchema.Catalog();
        IEnumerable<RemoteSchema.CatalogEntry> selected = all;
        if (onlyMethod is not null) {
            selected = all.Where(e => string.Equals(e.Method, onlyMethod, StringComparison.OrdinalIgnoreCase));
            if (!selected.Any())
                throw new Exception($"no command-port method named '{onlyMethod}' (try 'bal remote-schema' for the full list)");
        }

        var methods = selected.Select(e => new MethodInfo(
            e.Method,
            e.Signature,
            e.Params.Select(p => new ParamInfo(p.Name, p.Kind, p.Required)).ToList())).ToList();

        Json.Write(new RemoteSchemaResult(methods.Count, methods));
        return 0;
    }

    static string Next(string[] args, ref int i, string flag) {
        if (i + 1 >= args.Length) throw new CliUsageException($"{flag} needs a value");
        return args[++i];
    }

    record RemoteSchemaResult(int count, List<MethodInfo> methods);
    record MethodInfo(string method, string signature, List<ParamInfo> @params);
    record ParamInfo(string name, string kind, bool required);
}
