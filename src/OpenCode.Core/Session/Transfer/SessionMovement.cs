namespace OpenCode.Core.Session.Transfer;

using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Locations;
using OpenCode.Schema;

public enum SessionMoveDestinationFailure { NotFound, NotDirectory, Unavailable }

public sealed class SessionMoveDestinationException(string directory, SessionMoveDestinationFailure failure, Exception? inner = null)
    : IOException($"Move destination is {failure}: {directory}", inner)
{
    public string Directory { get; } = directory;
    public SessionMoveDestinationFailure Failure { get; } = failure;
}

/// <summary>A missing source moves immediately; otherwise Item is the durable pending control.</summary>
public sealed record SessionMoveAdmission(SessionInboxItem? Item, bool Moved);

/// <summary>Returned only after inbox delivery and placement projection commit together.</summary>
public sealed record SessionMoveDelivery(SessionInboxItem Item);

/// <summary>
/// Implicit-local Session.move. Reuses the host's authoritative Location map and inbox lock.
/// Does not copy files, change worktrees, interrupt execution, or reset instructions.
/// </summary>
public sealed class SessionMovement(IDatabase database, SessionStore sessions, PermissionLocationMap locations)
{
    public async Task RequestAsync(SessionMoveRequest request, SessionExecutionEngine execution,
        CancellationToken lifetime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Movement requires a host-owned execution lifetime.", nameof(lifetime));
        lifetime.ThrowIfCancellationRequested();
        await AdmitAsync(request, ct: ct).ConfigureAwait(true);
        // Publication is durable before the advisory wake. Request cancellation does not undo it.
        await execution.WakeAsync(request.SessionId, lifetime).ConfigureAwait(true);
    }

    /// <summary>Prepare and admit without scheduling. A supplied control ID reconciles pending moves only.</summary>
    public Task<SessionMoveAdmission> AdmitAsync(SessionMoveRequest request, MessageId? id = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Directory);
        if (request.Delivery is not (InboxDeliveryMode.Steer or InboxDeliveryMode.Queue))
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.WorkspaceId is not null)
            throw new NotSupportedException("Explicit workspace movement requires destination Location routing. No move was admitted.");

        return SessionRunCoordinator.AdmitAsync(request.SessionId, async () =>
        {
            var payload = await PrepareAsync(request, ct).ConfigureAwait(true);
            return await InboxSerialization.RunAsync(request.SessionId, async () =>
            {
                var latest = await sessions.GetSessionAsync(request.SessionId, ct).ConfigureAwait(true)
                    ?? throw new SessionMutationNotFoundException(request.SessionId);
                if (latest.Location.WorkspaceId is not null)
                    throw new NotSupportedException("Moving from an explicit workspace requires its source transport lifecycle.");
                var admission = new SessionAdmission(database);
                if (Stat(latest.Location.Directory, ct) != true)
                {
                    await admission.MoveFromMissingSourceLockedAsync(request.SessionId, payload, ct).ConfigureAwait(true);
                    return new SessionMoveAdmission(null, true);
                }
                return new SessionMoveAdmission(await admission.AdmitMoveLockedAsync(request.SessionId,
                    id ?? MessageId.Create(), payload, request.Delivery, ct).ConfigureAwait(true), false);
            }, ct).ConfigureAwait(true);
        }, ct);
    }

    /// <summary>
    /// Runner hook at Location-entry, idle, or safe step boundaries. The callback must close
    /// this session's source model transport, not invalidate the shared Location map.
    /// Null means the currently eligible item is absent or is not a move; re-evaluate input.
    /// </summary>
    public Task<SessionMoveDelivery?> TryDeliverAsync(SessionId sessionId, InboxPromotable scope,
        Func<CancellationToken, Task> closeSourceTransport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(closeSourceTransport);
        if (scope is not (InboxPromotable.Input or InboxPromotable.Steer)) throw new ArgumentOutOfRangeException(nameof(scope));
        return InboxSerialization.RunAsync(sessionId, () => new SessionAdmission(database)
            .DeliverMoveLockedAsync(sessionId, scope, closeSourceTransport, ct), ct);
    }

    public async Task<MoveInboxPayload> PrepareAsync(SessionMoveRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Directory);
        var current = await sessions.GetSessionAsync(request.SessionId, ct).ConfigureAwait(true)
            ?? throw new SessionMutationNotFoundException(request.SessionId);
        if (request.WorkspaceId is not null || current.Location.WorkspaceId is not null)
            throw new NotSupportedException("Explicit workspace movement requires Location routing. No move was admitted.");
        var value = request.Directory.Trim();
        var expanded = value == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : value.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]) : value;
        // Path.GetFullPath rejects empty input; Node path.resolve treats it as the current directory.
        var directory = Path.GetFullPath(expanded.Length == 0 ? "." : expanded, current.Location.Directory);
        var stat = Stat(directory, ct);
        if (stat is null) throw new SessionMoveDestinationException(directory, SessionMoveDestinationFailure.NotFound);
        if (!stat.Value) throw new SessionMoveDestinationException(directory, SessionMoveDestinationFailure.NotDirectory);

        try
        {
            var resolved = await CatalogLocation.ResolveAsync(database, directory, ct: ct).ConfigureAwait(true);
            var location = new LocationRef(directory);
            var lease = await locations.AcquireAsync(location, ct).ConfigureAwait(true);
            await using var leaseLifetime = lease.ConfigureAwait(true);
            if (lease.Location.WorkspaceId is not null || lease.Location.Project.Id != resolved.Project.Id)
                throw new InvalidOperationException("Destination Location disagrees with resolved project identity.");
            var relative = Path.GetRelativePath(resolved.Project.Directory, directory).Replace('\\', '/');
            return new MoveInboxPayload(location, resolved.Project.Id, relative == "." ? "" : relative);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new SessionMoveDestinationException(directory, SessionMoveDestinationFailure.Unavailable, error);
        }
    }

    private static bool? Stat(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return (File.GetAttributes(directory) & FileAttributes.Directory) != FileAttributes.None; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
