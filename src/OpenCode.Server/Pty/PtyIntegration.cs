namespace OpenCode.Server.Pty;

using OpenCode.Core.Pty;
using OpenCode.Schema;
using OpenCode.Server.Endpoints;

/// <summary>
/// Explicit composition boundary for PTY routes. Does not bypass authentication.
/// The host must supply its real credential/origin policy and Location-owned runtime.
/// </summary>
public sealed class PtyIntegration(
    PtyService runtime,
    PtyTickets tickets,
    WorkspaceId? workspaceId,
    Func<HttpContext, CancellationToken, ValueTask> requireAuthentication,
    Func<HttpRequest, bool> isAllowedOrigin,
    Func<CancellationToken, ValueTask> flushPlugins,
    Func<string, string, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> environment)
{
    public async ValueTask<PtyInfo> CreateAsync(HttpContext context, PtyCreateInput input)
    {
        await requireAuthentication(context, context.RequestAborted);
        await flushPlugins(context.RequestAborted);
        var cwd = string.IsNullOrEmpty(input.Cwd) ? runtime.Directory : input.Cwd;
        var values = new Dictionary<string, string>(input.Env ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in await environment(runtime.Directory, cwd, context.RequestAborted)) values[item.Key] = item.Value;
        return runtime.Create(input with { Cwd = cwd, Env = values });
    }

    public async ValueTask<PtyConnectToken> IssueTicketAsync(HttpContext context, PtyId id)
    {
        await requireAuthentication(context, context.RequestAborted);
        if (context.Request.Headers["x-opencode-ticket"] != "1" || !isAllowedOrigin(context.Request))
            throw new PtyForbiddenException("Invalid PTY connect token request");
        runtime.Get(id);
        return tickets.Issue(new(id, runtime.Directory, workspaceId));
    }

    /// <summary>Run before upgrading the socket. An invalid supplied ticket never falls back to credentials.</summary>
    public async ValueTask AuthorizeConnectAsync(HttpContext context, PtyId id)
    {
        runtime.Get(id);
        var ticket = RequestLocation.QueryValue(context.Request, "ticket") ?? "";
        if (ticket.Length == 0)
        {
            await requireAuthentication(context, context.RequestAborted);
            return;
        }
        if (!isAllowedOrigin(context.Request) || !tickets.Consume(ticket, new(id, runtime.Directory, workspaceId)))
            throw new PtyForbiddenException("Invalid PTY connect ticket.");
    }
}
