namespace OpenCode.Core.Commands;

using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using OpenCode.Core.Mcp;
using OpenCode.Schema;

public sealed record RuntimeCommand(CommandInfo Info,
    Func<CommandInvocation, CancellationToken, Task<PreparedCommand>> PrepareAsync);

/// <summary>Ordered command transform map. Later definitions replace callbacks and metadata together.</summary>
public sealed class CommandRuntime(IEnumerable<RuntimeCommand> definitions)
{
    private readonly Dictionary<string, RuntimeCommand> _commands = Build(definitions);

    /// <summary>Location startup/reload composition: internal pre, external plugins, internal post.</summary>
    public static CommandRuntime Create(string projectDirectory, CommandCatalog catalog, CommandPreparationOwner owner,
        IEnumerable<McpDiscoveredPrompt> prompts, McpRuntime mcp, IEnumerable<RuntimeCommand>? plugins = null) =>
        new(BuiltinCommands.Definitions(projectDirectory).Concat(Mcp(prompts, mcp)).Concat(plugins ?? []).Concat(Local(catalog, owner)));

    public IReadOnlyList<CommandInfo> List() => _commands.Values.Select(command => command.Info).ToArray();
    public CommandInfo? Get(string name) => _commands.GetValueOrDefault(name)?.Info;

    public async Task<PreparedCommand> PrepareAsync(string name, CommandInvocation invocation, CancellationToken ct = default)
    {
        var definition = _commands.GetValueOrDefault(name) ?? throw new CommandNotFoundException(name);
        try { return await definition.PrepareAsync(invocation, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CommandExecutionException) { throw; }
        catch (Exception error) { throw new CommandExecutionException(name, error); }
    }

    public static IEnumerable<RuntimeCommand> Local(CommandCatalog catalog, CommandPreparationOwner owner) =>
        catalog.List().Select(info => new RuntimeCommand(info, (input, ct) => catalog.PrepareAsync(info.Name, input, owner, ct)));

    /// <summary>Uses the existing Location MCP observation and client map; never discovers or connects servers.</summary>
    public static IEnumerable<RuntimeCommand> Mcp(IEnumerable<McpDiscoveredPrompt> prompts, McpRuntime runtime) =>
        prompts.Select(prompt =>
        {
            var name = Regex.Replace(prompt.Server, "[^a-zA-Z0-9_-]", "_", RegexOptions.NonBacktracking) + ":" + Regex.Replace(prompt.Prompt.Name, "[^a-zA-Z0-9_-]", "_", RegexOptions.NonBacktracking);
            return new RuntimeCommand(new CommandInfo(name, prompt.Prompt.Description), async (input, ct) =>
            {
                var args = CommandTemplate.ParseArguments(input.Prompt.Text);
                var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var argument in (prompt.Prompt.ProtocolPrompt.Arguments ?? []).Select((value, index) => (value, index)))
                    arguments[argument.value.Name] = argument.index < args.Length ? args[argument.index] : "";
                var result = await runtime.PromptAsync(prompt.Server, prompt.Prompt.Name, arguments, ct).ConfigureAwait(true);
                var text = CommandTemplate.Trim(string.Join("\n", result.Messages.Select(message => message.Content is TextContentBlock content ? content.Text : "")));
                return new PreparedCommand(name, input with { Prompt = input.Prompt with { Text = text } });
            });
        });

    private static Dictionary<string, RuntimeCommand> Build(IEnumerable<RuntimeCommand> definitions)
    {
        var commands = new Dictionary<string, RuntimeCommand>(StringComparer.Ordinal);
        foreach (var definition in definitions) commands[definition.Info.Name] = definition;
        return commands;
    }
}
