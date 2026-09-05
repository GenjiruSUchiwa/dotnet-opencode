namespace OpenCode.Core.Session;

using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Instructions;
using OpenCode.Core.Snapshot;
using OpenCode.Schema;

public sealed class SessionRevertMessageNotFoundException(SessionId sessionId, MessageId messageId) : InvalidOperationException("Revert boundary message not found.")
{
    public SessionId SessionId { get; } = sessionId;
    public MessageId MessageId { get; } = messageId;
}

/// <summary>Source session/revert.ts. Files come only from ordered assistant snapshot facts.</summary>
public sealed class SessionRevertOperations(IDatabase database, SessionStore sessions, SessionExecutionEngine execution, SessionSnapshotLocations snapshots)
{
    public Task<SessionRevert> StageAsync(SessionId sessionId, MessageId messageId, bool files = true, CancellationToken ct = default) =>
        SessionRunCoordinator.WithIdleOperationAsync(sessionId, async token =>
        {
            var session = await sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
            RequirePlugins(session.Location);
            var snapshot = await snapshots.TryGetAsync(session.Location, token).ConfigureAwait(false);
            var original = session.Revert?.Snapshot ?? (snapshot is null ? null : await snapshot.CaptureAsync(token).ConfigureAwait(false));
            var next = await new SessionRevertPersistence(database).PlanAsync(sessionId, messageId, token).ConfigureAwait(false);
            var restore = new Dictionary<string, SnapshotId>(StringComparer.Ordinal);
            if (original is { } saved)
                foreach (var file in session.Revert?.Files ?? []) restore[file.File] = saved;
            if (files) foreach (var file in next) restore[file.Key] = file.Value;
            if (restore.Count > 0)
                await (snapshot ?? throw new SnapshotException("restore", "The snapshot Location is unavailable")).RestoreAsync(restore, token).ConfigureAwait(false);
            var changes = original is { } before
                ? await (snapshot ?? throw new SnapshotException("diff", "The snapshot Location is unavailable")).DiffAsync(before,
                    await snapshot.CaptureAsync(token).ConfigureAwait(false) ?? before, files ? next.Keys.ToArray() : [], ct: token).ConfigureAwait(false)
                : [];
            var revert = new SessionRevert(messageId, Snapshot: original, Files: changes);
            await new SessionRevertPersistence(database).StageAsync(sessionId, revert, token).ConfigureAwait(false);
            return revert;
        }, ct);

    public async Task ClearAsync(SessionId sessionId, CancellationToken lifetime, CancellationToken ct = default)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Revert clear requires the host execution lifetime.", nameof(lifetime));
        await SessionRunCoordinator.WithIdleOperationAsync(sessionId, async token =>
        {
            var session = await sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
            RequirePlugins(session.Location);
            if (session.Revert is null) return false;
            if (session.Revert.Snapshot is { } original)
            {
                var snapshot = await snapshots.GetAsync(session.Location, token).ConfigureAwait(false);
                await snapshot.RestoreAsync((session.Revert.Files ?? []).ToDictionary(file => file.File, _ => original, StringComparer.Ordinal), token).ConfigureAwait(false);
            }
            await new SessionRevertPersistence(database).ClearAsync(sessionId, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await execution.WakeAsync(sessionId, lifetime).ConfigureAwait(false);
    }

    public Task CommitAsync(SessionId sessionId, CancellationToken ct = default) =>
        SessionRunCoordinator.WithIdleOperationAsync(sessionId, async token =>
        {
            var session = await sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
            await CommitPreparedAsync(database, session, token).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Wire into SessionPromptPreparation. Its caller has prepared new input and owns admission ordering.</summary>
    public static Task CommitPreparedAsync(IDatabase database, SessionInfo session, CancellationToken ct) => session.Revert is null
        ? Task.CompletedTask : new SessionRevertPersistence(database).CommitAsync(session.Id, session.Revert.MessageId, ct);

    private static void RequirePlugins(LocationRef location)
    {
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var producers = ProducerConfiguration.Read(location.Directory, home, Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory()), ConfigLoader.LoadDocument(directory: location.Directory));
        if (!producers.ReferencesAvailable) throw new IOException("Revert plugin configuration is unavailable.");
        producers.RequireNoPluginSources();
    }
}
