namespace BallisticEngine.Cli;

// One `bal` verb. Run() receives the args AFTER the verb and returns the process exit code (0 ok,
// 1 handled error). Throw CliUsageException for a usage error (exit 2 + Usage printed).
internal interface ICommand {
    string Name { get; }
    string Summary { get; }   // one-line, shown in `bal --help`
    string Usage { get; }     // multi-line usage, shown on a usage error
    int Run(string[] args);
}

// Thrown by a command when its arguments are malformed — Program maps it to exit code 2 + Usage.
internal sealed class CliUsageException(string message) : Exception(message);
