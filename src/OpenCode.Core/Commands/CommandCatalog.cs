namespace OpenCode.Core.Commands;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Schema;

public sealed record LocalCommand(string Name, string Template, string? Description = null,
    string? Agent = null, ConfigModelSelection? Model = null, bool? Subtask = null, string? Source = null);

/// <summary>A Location-scoped snapshot. Rebuild when config or command source files change.</summary>
public sealed class CommandCatalog
{
    private readonly Dictionary<string, LocalCommand> _commands = new(StringComparer.Ordinal);

    public CommandCatalog(IEnumerable<LocalCommand> commands)
    {
        foreach (var command in commands) _commands[command.Name] = command;
    }

    public IReadOnlyList<CommandInfo> List() => _commands.Values.Select(command => new CommandInfo(command.Name, command.Description)).ToArray();
    public CommandInfo? Get(string name) => _commands.TryGetValue(name, out var command) ? new(name, command.Description) : null;
    public LocalCommand? GetDefinition(string name) => _commands.GetValueOrDefault(name);

    public static async Task<CommandCatalog> LoadAsync(string directory, CancellationToken ct = default)
    {
        var location = Path.GetFullPath(directory);
        var snapshot = await ConfigLoader.LoadSnapshotAsync(location, ct);
        var commands = new List<LocalCommand>();
        foreach (var source in snapshot.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source is ConfigSource.Document document)
            {
                foreach (var key in new[] { "plugin", "plugins" })
                    if (document.Info[key] is { } value && value is not JsonArray { Count: 0 } && value is not JsonObject { Count: 0 })
                        throw new NotSupportedException("Configured plugins require the native plugin runtime before command catalogs can be complete.");
                commands.AddRange(await CommandDocuments.LoadAsync([(document.Path, document.Info)], [], ct));
                continue;
            }
            if (source is not ConfigSource.Discovery { Entry: ConfigDirectory root }) continue;
            foreach (var name in new[] { "plugin", "plugins" })
            {
                var path = Path.Combine(root.Path, name);
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 && Directory.EnumerateFileSystemEntries(path).Any())
                        throw new NotSupportedException("Auto-discovered plugins require the native plugin runtime before command catalogs can be complete.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            commands.AddRange(await CommandDocuments.LoadAsync([], [root.Path], ct));
        }
        return new CommandCatalog(commands);
    }

    /// <summary>Applies Session selections before interpolation, as ConfigCommandPlugin does. Does not admit a prompt.</summary>
    public async Task<PreparedCommand> PrepareAsync(string name, CommandInvocation invocation, CommandPreparationOwner owner,
        CancellationToken ct = default)
    {
        var command = GetDefinition(name) ?? throw new CommandNotFoundException(name);
        try
        {
            ct.ThrowIfCancellationRequested();
            // The owner switches only when needed, then returns the selected agent's current model.
            var agentModel = command.Agent is null ? null : await owner.SelectAgentAsync(invocation.SessionId, command.Agent, ct);
            var model = command.Model is { } selection ? new ModelRef(selection.ProviderId, selection.Model, selection.Variant) : agentModel;
            if (model is not null) await owner.SelectModelAsync(invocation.SessionId, model, ct);
            var text = await CommandTemplate.EvaluateAsync(command.Template, invocation.Prompt.Text,
                source => owner.InterpolateShellAsync(new ShellInterpolation(invocation.SessionId, owner.Directory, source), ct), ct);
            return new PreparedCommand(name, invocation with { Prompt = invocation.Prompt with { Text = text } });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new CommandExecutionException(name, error); }
    }
}

public sealed record CommandInvocation(SessionId SessionId, PromptInput Prompt, InboxDeliveryMode Delivery = InboxDeliveryMode.Steer);
public sealed record ShellInterpolation(SessionId SessionId, string Directory, string Source);

/// <summary>Callbacks must target the invocation's Location/Session, not an arbitrary command executor.</summary>
public sealed record CommandPreparationOwner(
    string Directory,
    Func<SessionId, string, CancellationToken, Task<ModelRef?>> SelectAgentAsync,
    Func<SessionId, ModelRef, CancellationToken, Task> SelectModelAsync,
    Func<ShellInterpolation, CancellationToken, Task<string>> InterpolateShellAsync);

public sealed record PreparedCommand(string Name, CommandInvocation Invocation)
{
    /// <summary>The Session owner resolves attachments/instructions and durably admits, then wakes execution.</summary>
    public Task AdmitAsync(Func<CommandInvocation, CancellationToken, Task> admit, CancellationToken ct = default) => admit(Invocation, ct);
}

public sealed class CommandNotFoundException(string command) : Exception($"Command not found: {command}")
{
    public string Command { get; } = command;
}

public sealed class CommandExecutionException(string command, Exception inner) : Exception(inner.Message, inner)
{
    public string Command { get; } = command;
}
