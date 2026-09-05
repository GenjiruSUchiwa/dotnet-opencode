namespace OpenCode.Server.Services;

using System.Runtime.ExceptionServices;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Transfer;
using OpenCode.Core.Session.Subagents;
using OpenCode.Core.Jobs;
using OpenCode.Core.Database;
using OpenCode.Core.Shell.Jobs;
using OpenCode.Core.Session.Skills;
using OpenCode.Schema;

/// <summary>
/// Owns the lifetime of Core calls, not session execution ownership. Core remains
/// responsible for joining resumes, coalescing wakes, interruption, and settlement.
/// Register the same singleton as this service and as IHostedService.
/// </summary>
public sealed class SessionExecutionService(
    SessionExecutionEngine engine,
    IHostApplicationLifetime lifetime,
    ILogger<SessionExecutionService> logger,
    IEventFeedService feed,
    SessionMovement? movement = null, SessionRevertOperations? reverts = null, SessionSubagents? subagents = null,
    SessionTitleService? titles = null, JobRuntime? jobs = null, SessionStore? sessions = null,
    ShellToolJobs? shellJobs = null, SessionSkillService? skills = null) : IHostedService, IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
    private readonly HashSet<Task<Exception?>> _running = [];
    private readonly SessionEventBridge _events = new(feed);
    private Task? _recovery;
    public SessionRecoveryReport? RecoveryReport { get; private set; }
    public Exception? RecoveryFailure { get; private set; }
    public IReadOnlyList<MessageId>? RecoveredShellNotifications { get; private set; }
    private bool _started;
    private bool _stopping;
    private int _disposed;

    public SessionExecutionCapabilities Capabilities
    {
        get
        {
            lock (_gate)
            {
                var capabilities = engine.Capabilities;
                var reason = !_started || _stopping || _shutdown.IsCancellationRequested
                    ? "The session execution host is not accepting work."
                    : !_events.IsRunning ? "The session event bridge is unavailable."
                    : !capabilities.CanExecute ? capabilities.UnavailableReason ?? "Native session execution is unavailable."
                    : !capabilities.Instructions ? "Normal server execution requires native instructions."
                    : null;
                return capabilities with { CanExecute = reason is null, UnavailableReason = reason };
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("The execution host cannot restart after shutdown.");
            if (_started) return Task.CompletedTask;
            _events.Start();
            _started = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>Managed startup only, after election and prior-owner verification. Never called by SDK hosts.</summary>
    internal Task StartRecovery()
    {
        lock (_gate)
        {
            RequireRecordingReady();
            return _recovery ??= RecoverCoreAsync();
        }
    }

    private async Task RecoverCoreAsync()
    {
        // The combined sweep acknowledges ownership/accounting, not model/tool
        // completion. Finish this startup phase before ordinary HTTP admission;
        // its recovered drains keep running on the shared host lifetime.
        try
        {
            if (subagents is null) throw new NotSupportedException("Managed restart recovery requires the shared subagent jobs service.");
            if (sessions is null || jobs is null || shellJobs is null)
                throw new NotSupportedException("Managed restart recovery requires the shared durable Job and Shell services.");
            // Freeze the durable roots and recoverable child IDs before notices
            // can schedule work. Runtime active IDs are exclusions, never a
            // replacement for this persisted restart snapshot.
            var suspended = (await sessions.ListSuspendedAsync(_shutdown.Token)).ToHashSet();
            foreach (var marker in await jobs.PendingBackgroundAsync(_shutdown.Token))
                if (marker.Status == JobStatus.Running && marker.Recovery is JobSubagentRecovery child && marker.Id == child.ChildSessionId.Value)
                    suspended.Add(child.ChildSessionId);
            suspended.ExceptWith(engine.ActiveSessionIds);
            RecoveredShellNotifications = await shellJobs.RecoverAfterConfirmedRestartAsync(suspended, _shutdown.Token);
            // This entrypoint includes root accounting and reattaches the same
            // recovered child drains. Never invoke a second engine sweep/resume.
            var report = await subagents.RecoverSuspendedAsync(maxAttempts: 10);
            RecoveryReport = report;
            foreach (var blocked in report.Blocked)
                logger.LogWarning("Session restart recovery blocked for {SessionId}: {Reason}", blocked.Key.Value, blocked.Value);
            foreach (var exhausted in report.Exhausted)
                logger.LogWarning("Session restart recovery attempts exhausted for {SessionId}", exhausted.Value);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            RecoveryFailure = error;
            logger.LogError(error, "Session startup recovery failed");
        }
    }

    public void RequireReady()
    {
        var capabilities = Capabilities;
        if (!capabilities.CanExecute) throw new NotSupportedException(capabilities.UnavailableReason);
    }

    public async Task CheckReadinessAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        RequireReady();
        await engine.CheckReadinessAsync(sessionId, cancellationToken);
    }

    public void RequireRecordingReady()
    {
        lock (_gate)
            if (!_started || _stopping || _shutdown.IsCancellationRequested || !_events.IsRunning)
                throw new NotSupportedException("The session event bridge is not accepting work.");
    }

    /// <summary>Advisory scheduling acknowledges Core ownership, not model completion.</summary>
    public async Task WakeAsync(SessionId sessionId)
    {
        if (await Schedule(sessionId, token => engine.WakeAsync(sessionId, token)) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Request cancellation stops waiting; host cancellation owns execution and settlement.</summary>
    public async Task ResumeAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        await CheckReadinessAsync(sessionId, cancellationToken);
        if (await Schedule(sessionId, token => engine.ResumeHostedAsync(sessionId, token)).WaitAsync(cancellationToken) is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async Task MoveAsync(SessionMoveRequest request, CancellationToken ct)
    {
        RequireRecordingReady();
        if (movement is null) throw new NotSupportedException("The shared SessionMovement service is not configured; no move was admitted.");
        if (await Schedule(request.SessionId, lifetime => movement.RequestAsync(request, engine, lifetime, ct)) is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async Task ClearRevertAsync(SessionId sessionId, CancellationToken ct)
    {
        RequireRecordingReady();
        if (reverts is null) throw new NotSupportedException("The Session revert service is not configured.");
        if (await Schedule(sessionId, lifetime => reverts.ClearAsync(sessionId, lifetime, ct), recordingOnly: true) is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private Task<Exception?> Schedule(SessionId sessionId, Func<CancellationToken, Task> start, bool recordingOnly = false)
    {
        var accepted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?>? running = null;
        lock (_gate)
        {
            if (recordingOnly) RequireRecordingReady();
            else RequireReady();
            running = RunAsync();
            _running.Add(running);
        }
        return accepted.Task;

        async Task<Exception?> RunAsync()
        {
            // Register the owned task before Core can complete synchronously.
            await Task.Yield();
            try
            {
                await start(_shutdown.Token);
                accepted.TrySetResult(null);
                return null;
            }
            catch (OperationCanceledException error)
            {
                accepted.TrySetResult(error);
                return error;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Session execution failed for {SessionId}", sessionId.Value);
                accepted.TrySetResult(error);
                return error;
            }
            finally
            {
                // Wake acknowledges registration. Core still owns the drain and
                // its bounded settlement, including any coalesced successor.
                await engine.AwaitIdleAsync(sessionId, CancellationToken.None);
                lock (_gate) _running.Remove(running!);
            }
        }
    }

    public Task<bool> InterruptAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        engine.InterruptAsync(sessionId, cancellationToken);

    public Task<bool> InterruptAsync(SessionId sessionId, SessionInterruptOptions options, CancellationToken cancellationToken) =>
        engine.InterruptAsync(sessionId, options, _shutdown.Token, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task<Exception?>[] running;
        lock (_gate)
        {
            _stopping = true;
            running = _running.ToArray();
        }
        await _shutdown.CancelAsync();
        if (_recovery is not null)
        {
            try { await _recovery; }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception error) { logger.LogError(error, "Session startup recovery failed"); }
            // Recovery schedules Core-owned drains outside Schedule(). Keep the bridge
            // alive through their shutdown settlement, including partial recovery failure.
            await Task.WhenAll(engine.ActiveSessionIds.Select(id => engine.AwaitIdleAsync(id, CancellationToken.None)));
        }
        // Settlement is Core-owned and bounded independently of work cancellation.
        // Do not abandon it when the host's shutdown deadline expires.
        await Task.WhenAll(running);
        if (jobs is not null) await jobs.DisposeAsync();
        if (subagents is not null) await subagents.DisposeAsync();
        if (titles is not null) await titles.DisposeAsync();
        if (skills is not null) await skills.DisposeAsync();
        // Keep forwarding terminal commits until all owned drains have settled.
        await _events.StopAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { StopAsync(default).GetAwaiter().GetResult(); }
        finally { _shutdown.Dispose(); }
    }
}
