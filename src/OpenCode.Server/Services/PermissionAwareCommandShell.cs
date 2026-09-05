namespace OpenCode.Server.Services;

using System.Text;
using OpenCode.Core.Commands;
using OpenCode.Core.Agent;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Core.Session;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Command interpolation uses the same Location policy and permissions, without inventing a model tool call.</summary>
public sealed class PermissionAwareCommandShell(SessionStore sessions, ToolLocationFactory factory, PermissionLocationMap locations)
    : IPermissionAwareCommandShell
{
    public async Task<string> InterpolateAsync(ShellInterpolation input, CancellationToken ct)
    {
        var session = await sessions.GetSessionAsync(input.SessionId, ct) ?? throw new SessionMutationNotFoundException(input.SessionId);
        if (PermissionLocationMap.Canonical(session.Location) != PermissionLocationMap.Canonical(new(input.Directory)))
            throw new NotSupportedException("The Session Location changed during command preparation; retry the command at its current Location.");
        await using var lease = await factory.AcquireAsync(locations, session.Location, ct);
        var agent = await AgentCatalog.ResolveAsync(session.Location.Directory, session.Agent is null ? null : AgentId.FromExisting(session.Agent), ct)
            ?? throw new NotSupportedException("The command's permission agent is unavailable.");
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var policy = new LocalShellPolicy(new LocalToolLocation(lease.Location.Directory, lease.Location.Project.Directory, home),
            new CommandPermission(lease.Permissions, session.Id, agent.Id));
        // Only Session/Agent are meaningful to this scanner adapter. No message or
        // tool caller exists, and CommandPermission never publishes those fields.
        var context = new ToolContext(session.Id, agent.Id, null, "",
            _ => throw new NotSupportedException("Command interpolation has no model tool-progress channel."));
        var prepared = await policy.PrepareAsync(input.Source, input.Directory, context, ct);
        var capture = new ShellProcessSource(Path.Combine(ConfigLoader.GetDefaultDataDirectory(), "shell", "commands"), sessions.Clock);
        // Source interpolation has no timeout and inherits the host environment.
        // Nonzero exit is still output, as AppProcess.run does not enforce success.
        var result = await capture.RunAsync(prepared, 0, ct);
        // Tool previews add truncation/no-output markers. A command template needs
        // the exact combined UTF-8 bytes, including empty output and a possible BOM.
        return Encoding.UTF8.GetString(await File.ReadAllBytesAsync(result.File, ct));
    }

    private sealed class CommandPermission(PermissionService permissions, SessionId session, AgentId agent) : IToolPermission
    {
        public Task AssertAsync(string action, IReadOnlyList<string> resources, IReadOnlyList<string> save,
            ToolContext context, IReadOnlyDictionary<string, object>? metadata, CancellationToken ct) =>
            permissions.AssertAsync(new PermissionAskInput(session, action, resources, Agent: agent, Save: save), ct);
    }
}
