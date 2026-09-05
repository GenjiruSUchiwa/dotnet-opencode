namespace OpenCode.Core.Session;

using OpenCode.Core.Database;
using OpenCode.Core.Jobs;
using OpenCode.Schema;

/// <summary>Source Session.background over every job blocking this Session in the shared host JobRuntime.</summary>
public sealed class SessionBackgroundService
{
    private readonly SessionStore _sessions;
    private readonly JobRuntime _jobs;
    private readonly SessionExecutionEngine _execution;
    private readonly CancellationToken _lifetime;

    public SessionBackgroundService(SessionStore sessions, JobRuntime jobs, SessionExecutionEngine execution, CancellationToken lifetime)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Session backgrounding requires the host execution lifetime.", nameof(lifetime));
        _sessions = sessions; _jobs = jobs; _execution = execution; _lifetime = lifetime;
    }

    public async Task BackgroundAsync(SessionId sessionId, CancellationToken ct = default)
    {
        _ = await _sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
        var promoted = await _jobs.BackgroundAllAsync(sessionId, ct: ct).ConfigureAwait(false);
        if (promoted.Count == 0) return;
        var text = string.Join('\n', new[]
        {
            "User requested that active blocking work be moved to the background.", "", "Backgrounded work:"
        }.Concat(promoted.Select(job => $"- {job.Type}: {(job.Title is { Length: > 0 } title ? title : job.Id)}")).Concat(new[]
        {
            "", "The backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation."
        }));
        await _sessions.AdmitInboxAsync(sessionId, MessageId.Create(), new SyntheticInboxPayload(text), ct: ct).ConfigureAwait(false);
        var current = await _sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
        if (current.Revert is null) await _execution.WakeAsync(sessionId, _lifetime).ConfigureAwait(false);
    }
}
