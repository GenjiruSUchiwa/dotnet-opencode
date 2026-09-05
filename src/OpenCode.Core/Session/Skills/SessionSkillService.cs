namespace OpenCode.Core.Session.Skills;

using System.Diagnostics;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Schema;

/// <summary>Standalone user skill activation. No empty prompt, model tool executor, fake SkillMessage, or independent runner.</summary>
public sealed class SessionSkillService : IAsyncDisposable
{
    private readonly SessionStore _sessions;
    private readonly ISessionSkillPublisher _publisher;
    private readonly SessionExecutionEngine _execution;
    private readonly CancellationTokenSource _shutdown;
    private readonly CancellationToken _lifetime;
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _resumes = [];
    private bool _closed;

    public SessionSkillService(SessionStore sessions, ISessionSkillPublisher publisher,
        SessionExecutionEngine execution, CancellationToken hostLifetime)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(execution);
        if (!hostLifetime.CanBeCanceled) throw new ArgumentException("Skill activation requires an owned host lifetime.", nameof(hostLifetime));
        _sessions = sessions;
        _publisher = publisher;
        _execution = execution;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime);
        _lifetime = _shutdown.Token;
    }

    public async Task ActivateAsync(SessionId sessionId, SessionSkillRequest input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Skill.IsInitialized()) throw new ArgumentException("Skill must be an initialized string identifier.", nameof(input));
        lock (_gate) ObjectDisposedException.ThrowIf(_closed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        var session = await _sessions.GetSessionAsync(sessionId, linked.Token) ?? throw new SessionMutationNotFoundException(sessionId);
        if (session.Location.WorkspaceId is not null)
            throw new NotSupportedException("Skill activation requires the skill registry for the Session's execution Location.");
        // This is the same complete local producer used by prompt preparation and the skill list
        // endpoint. Configured/discovered plugin and remote-source gaps are errors, not partial lists.
        var skills = await InstructionCatalog.ListSkillsAsync(session.Location.Directory, linked.Token);
        var skill = skills.FirstOrDefault(item => item.Id == input.Skill) ?? throw new SessionSkillNotFoundException(input.Skill);
        var eventId = input.Id is { } id ? EventId.FromExisting(id.Value is { } value && value.StartsWith("msg_", StringComparison.Ordinal)
            ? "evt_" + value[4..] : id.Value) : (EventId?)null;
        await _publisher.PublishAsync(new SessionSkillActivatedData(sessionId, skill.Id, skill.Name, skill.Content), eventId, linked.Token);
        if (input.Resume == false) return;

        // Publication is committed before scheduling. Use the existing forced resume/join, not
        // advisory wake (which can idle without inbox work), and do not bind it to HTTP cancellation.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _lifetime.ThrowIfCancellationRequested();
            _resumes.Add(finished.Task);
        }
        _ = ResumeAsync(sessionId, finished);
    }

    private async Task ResumeAsync(SessionId sessionId, TaskCompletionSource finished)
    {
        try { await _execution.ResumeHostedAsync(sessionId, _lifetime); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // The source ignores resume failure after the activation fact commits. Execution's
            // own events describe its result; never pretend the activation itself was rolled back.
            Trace.TraceWarning("Skill activation resume failed ({0}).", error.GetType().Name);
        }
        finally
        {
            lock (_gate) _resumes.Remove(finished.Task);
            finished.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] resumes;
        lock (_gate) { if (_closed) return; _closed = true; resumes = _resumes.ToArray(); }
        await _shutdown.CancelAsync();
        await Task.WhenAll(resumes);
        _shutdown.Dispose();
    }
}
