namespace BallisticEngine.Cli;

internal interface ICommand {
    string Name { get; }
    string Summary { get; }
    string Usage { get; }
    int Run(string[] args);
}
