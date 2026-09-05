namespace OpenCode.Server.Services;

using OpenCode.Core.Agent;
using OpenCode.Core.Commands;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Optional host boundary: configured shell selection, same-Location permission approval,
/// owned process lifetime, and combined UTF-8 output. Never register an unpermissioned executor.</summary>
public interface IPermissionAwareCommandShell
{
    Task<string> InterpolateAsync(ShellInterpolation input, CancellationToken ct);
}

public sealed class CommandHostLease(CommandRuntime runtime, ToolLocationLease location) : IAsyncDisposable
{
    public CommandRuntime Runtime { get; } = runtime;
    public LocationInfo Location => location.Location;
    public ValueTask DisposeAsync() => location.DisposeAsync();
}

/// <summary>One built-in/MCP/config command snapshot per operation, borrowing the shared tool Location.</summary>
public sealed class CommandHostService(SessionStore store, SessionMutations mutations,
    SessionExecutionEngine engine, SessionExecutionService execution, ToolLocationFactory factory,
    PermissionLocationMap locations, IPermissionAwareCommandShell? shell = null)
{
    public async Task<CommandHostLease> AcquireAsync(LocationRef reference, CancellationToken ct)
    {
        var location = await factory.AcquireAsync(locations, reference, ct);
        try
        {
            var catalog = await CommandCatalog.LoadAsync(location.Location.Directory, ct);
            var observation = await location.Mcp.ObserveAsync(InstructionCatalog.ReadMcpConfiguration(location.Location.Directory), ct);
            var owner = new CommandPreparationOwner(location.Location.Directory,
                async (id, agentId, token) =>
                {
                    var session = await store.GetSessionAsync(id, token) ?? throw new SessionMutationNotFoundException(id);
                    if (session.Agent != agentId) await mutations.SelectAgentAsync(id, agentId, token);
                    var agent = await AgentCatalog.ResolveAsync(session.Location.Directory, AgentId.FromExisting(agentId), token)
                        ?? throw new NotSupportedException("The command's selected agent is unavailable.");
                    return agent.Model;
                },
                (id, model, token) => mutations.SelectModelAsync(id, model, token),
                (input, token) => shell is null ? UnsupportedShell(input.Source) : shell.InterpolateAsync(input, token));
            var runtime = CommandRuntime.Create(location.Location.Project.Directory, catalog, owner, observation.Prompts, location.Mcp);
            // Metadata and callbacks come from this exact snapshot. Config overrides
            // builtins/MCP through Core's ordering, not a second Server command registry.
            var guarded = new CommandRuntime(runtime.List().Select(info => new RuntimeCommand(info, async (input, token) =>
            {
                // Arguments may introduce shell interpolation. Use Core's parser to
                // reject it before selection side effects when no approved executor exists.
                if (shell is null && catalog.GetDefinition(info.Name) is { } definition)
                    await CommandTemplate.EvaluateAsync(definition.Template, input.Prompt.Text, UnsupportedShell, token);
                return await runtime.PrepareAsync(info.Name, input, token);
            })));
            return new CommandHostLease(guarded, location);
        }
        catch
        {
            await location.DisposeAsync();
            throw;
        }
    }

    public async Task ExecuteAsync(SessionId sessionId, string command, PromptInput prompt, InboxDeliveryMode delivery, CancellationToken ct)
    {
        var session = await store.GetSessionAsync(sessionId, ct) ?? throw new SessionMutationNotFoundException(sessionId);
        await using var commandLocation = await AcquireAsync(session.Location, ct);
        if (commandLocation.Runtime.Get(command) is null) throw new CommandNotFoundException(command);
        try
        {
            execution.RequireRecordingReady();
            var prepared = await commandLocation.Runtime.PrepareAsync(command, new CommandInvocation(sessionId, prompt, delivery), ct);
            await prepared.AdmitAsync(async (input, token) =>
            {
                await engine.AdmitPromptAsync(input.SessionId, input.Prompt, delivery: input.Delivery, ct: token);
                await execution.WakeAsync(input.SessionId);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CommandExecutionException) { throw; }
        catch (Exception error) { throw new CommandExecutionException(command, error); }
    }

    private static Task<string> UnsupportedShell(string source) => Task.FromException<string>(
        new NotSupportedException("Permission-aware command shell interpolation is not configured; no shell was executed."));
}
