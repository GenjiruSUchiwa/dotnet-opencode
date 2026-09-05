namespace OpenCode.Core.Shell.Jobs;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Jobs;
using OpenCode.Core.Session;
using OpenCode.Schema;

/// <summary>
/// Real ShellTool adapter over the host's ONE generic JobRuntime and existing Session admission/
/// execution services. It owns no process registry, Job dictionary, database path, or model loop.
/// </summary>
public sealed class ShellToolJobs : IShellToolJobs
{
    private readonly JobRuntime _jobs;
    private readonly SessionStore _sessions;
    private readonly SessionExecutionEngine _execution;
    private readonly CancellationToken _lifetime;

    public ShellToolJobs(JobRuntime jobs, SessionStore sessions, SessionExecutionEngine execution, CancellationToken hostLifetime)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(execution);
        if (!hostLifetime.CanBeCanceled) throw new ArgumentException("Background shell jobs require a cancellable host lifetime.", nameof(hostLifetime));
        _jobs = jobs;
        _sessions = sessions;
        _execution = execution;
        _lifetime = hostLifetime;
    }

    public bool SupportsBackground => !_lifetime.IsCancellationRequested && !_jobs.IsClosed;

    public async Task<ShellJobInfo> StartAsync(ShellJobStart input, CancellationToken ct)
    {
        var info = await _jobs.StartAsync(new JobStartInput("shell", input.Run, input.Shell.Id.Value, input.Shell.Command,
            new Dictionary<string, JsonElement>
            {
                ["sessionID"] = JsonSerializer.SerializeToElement(input.SessionId.Value),
                ["shellID"] = JsonSerializer.SerializeToElement(input.Shell.Id.Value)
            }, new JobShellRecovery(input.SessionId, input.Shell.Id, input.Shell.Command)), ct);
        return Project(info);
    }

    public async Task<ShellJobBlock?> BlockAsync(string id, SessionId sessionId, CancellationToken ct)
    {
        var result = await _jobs.BlockAsync(id, sessionId, ct);
        return result is null ? null : new ShellJobBlock(Project(result.Info), result.Backgrounded);
    }

    public async Task<ShellJobInfo?> BackgroundAsync(string id, CancellationToken ct)
    {
        var result = await _jobs.BackgroundAsync(id, ct);
        return result is null ? null : Project(result);
    }

    /// <summary>Source foreground promotion; resolves the blocked ShellTool only after markers are committed.</summary>
    public async Task<IReadOnlyList<ShellJobInfo>> BackgroundAllAsync(SessionId sessionId, CancellationToken ct = default) =>
        (await _jobs.BackgroundAllAsync(sessionId, "shell", ct)).Select(Project).ToArray();

    public async Task<ShellJobInfo?> WaitAsync(string id, CancellationToken ct)
    {
        var result = await _jobs.WaitAsync(id, ct: ct);
        return result.Info is null ? null : Project(result.Info);
    }

    public async Task CancelAsync(string id, CancellationToken ct) => await _jobs.CancelAsync(id, ct);

    public async Task AdmitCompletionAsync(SessionId sessionId, MessageId notificationId, ShellNotification notification, CancellationToken ct)
    {
        if (!notification.Metadata.TryGetValue("jobID", out var id) || id.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Shell completion requires the actual job ID.");
        var job = await _jobs.GetAsync(id.GetString()!, ct);
        if (job is null || job.Type != "shell" || job.Status == JobStatus.Running || job.NotificationId != notificationId
            || job.Metadata?.TryGetValue("sessionID", out var owner) != true || owner.GetString() != sessionId.Value
            || job.Metadata?.TryGetValue("shellID", out var shell) != true
            || !notification.Metadata.TryGetValue("shellID", out var supplied) || shell.GetString() != supplied.GetString())
            throw new InvalidOperationException("Shell completion does not match its terminal job and Session identity.");
        await AdmitAsync(sessionId, notificationId, notification, suppressWake: false, ct);
    }

    public Task CompleteBackgroundAsync(MessageId notificationId, CancellationToken ct) => _jobs.CompleteBackgroundAsync(notificationId, ct);

    /// <summary>
    /// Explicit startup-only operation. Caller must have confirmed predecessor death and own the
    /// managed registration lock, just like SessionExecutionEngine.RecoverSuspendedAsync. Never
    /// called by construction, tool registration, normal connect, or an unregistered shared-DB host.
    /// </summary>
    public async Task<IReadOnlyList<MessageId>> RecoverAfterConfirmedRestartAsync(IReadOnlySet<SessionId> suspended, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        var recovered = new List<MessageId>();
        foreach (var background in await _jobs.PendingBackgroundAsync(linked.Token))
        {
            if (background.Recovery is not JobShellRecovery shell) continue;
            background.Validate();
            if (await _jobs.GetAsync(background.Id, linked.Token) is { Status: JobStatus.Running }) continue;
            // A running marker is not an exit code or evidence of a surviving process. No PID,
            // output file, or fabricated ShellInfo is used to infer success after a restart.
            var state = background.Status switch
            {
                JobStatus.Running or JobStatus.Cancelled => "cancelled",
                JobStatus.Completed => "completed",
                JobStatus.Error => "error",
                _ => throw new InvalidOperationException("Invalid persisted job state.")
            };
            var text = background.Status == JobStatus.Running ? "Command cancelled because the server restarted"
                : background.Status == JobStatus.Completed ? background.Output ?? "Command completed"
                : background.Status == JobStatus.Error ? background.Error ?? "Command failed" : "Command cancelled";
            try
            {
                await AdmitAsync(shell.SessionId, background.NotificationId,
                    ShellNotification.Background(background.Id, shell.ShellId, shell.Command, state, text),
                    suspended.Contains(shell.SessionId), linked.Token);
            }
            catch (SessionMutationNotFoundException) { /* Source recovery removes markers whose receiving Session was deleted. */ }
            await _jobs.CompleteBackgroundAsync(background.NotificationId, linked.Token);
            recovered.Add(background.NotificationId);
        }
        return recovered;
    }

    private async Task AdmitAsync(SessionId sessionId, MessageId notificationId, ShellNotification notification, bool suppressWake, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        _ = await _sessions.GetSessionAsync(sessionId, linked.Token) ?? throw new SessionMutationNotFoundException(sessionId);
        var existing = await _sessions.ReconcileInboxAsync(sessionId, notificationId, "synthetic", ct: linked.Token);
        if (existing is null)
            await _sessions.AdmitInboxAsync(sessionId, notificationId,
                new SyntheticInboxPayload(notification.Text, notification.Description,
                    notification.Metadata.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal)), ct: linked.Token);
        var current = await _sessions.GetSessionAsync(sessionId, linked.Token) ?? throw new SessionMutationNotFoundException(sessionId);
        if (!suppressWake && current.Revert is null) await _execution.WakeAsync(sessionId, _lifetime);
    }

    private static ShellJobInfo Project(JobInfo info) => new(info.Id, info.Status switch
    {
        JobStatus.Running => ShellJobStatus.Running,
        JobStatus.Completed => ShellJobStatus.Completed,
        JobStatus.Error => ShellJobStatus.Error,
        JobStatus.Cancelled => ShellJobStatus.Cancelled,
        _ => throw new InvalidOperationException("Unknown job state.")
    }, info.NotificationId, info.Error);
}
