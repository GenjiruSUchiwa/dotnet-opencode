namespace OpenCode.Cli.Commands.Api;

public sealed record ApiOptions(IReadOnlyList<string> Request, string? Data, IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Parameters, string? Server, bool Standalone);
