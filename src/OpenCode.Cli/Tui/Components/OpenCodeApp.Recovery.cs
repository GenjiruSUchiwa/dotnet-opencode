namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Recovery;
using OpenCode.Cli.Tui.Theme;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionFeedSnapshot?>? ReadRecoveryFeed { get; set; }
    [Parameter] public Func<CancellationToken, Task>? RetryRecoveryFeed { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task<SessionObservationSnapshot>>? ReloadRecoverySession { get; set; }
    [Parameter] public Func<LocationRecoveryEvidence, CancellationToken, Task>? ChooseRecoveryDirectory { get; set; }
    [Parameter] public Func<ProjectId, LocationRef, CancellationToken, Task<RecoveryDirectoryPage>>? LoadRecoveryDirectories { get; set; }
    [Parameter] public Func<LocationRef, CancellationToken, Task<RecoveryDirectoryPage>>? BrowseRecoveryDirectory { get; set; }
    [Parameter] public Func<SessionId, LocationRef, InboxDeliveryMode?, CancellationToken, Task<SessionMoveSnapshot>>? SubmitRecoveryMove { get; set; }
    [Parameter] public string RecoveryHome { get; set; } = "";
    private SessionFeedSnapshot? _recoveryFeed;
    private SessionObservationSnapshot? _recoverySession;
    private LocationRecoveryEvidence? _recoveryLocation;
    private SessionId? _recoveryTarget;
    private LocationRef? _recoveryTargetLocation;
    private CancellationTokenSource? _recoveryWait;
    private Task _recoveryTask = Task.CompletedTask;
    private string? _recoveryWaitError;
    private long _recoveryGeneration;
    private bool _recoveryStopped;
    private SessionInfo? _recoveryMovePicker;
    private bool CanChooseRecoveryDirectory => _recoveryLocation?.Location.WorkspaceId is null
        && (SubmitRecoveryMove is not null || ChooseRecoveryDirectory is not null);
    private SessionMoveSnapshot? RecoveryMove => _recoverySession?.Move;
    private SessionMoveSnapshot? PickerMove => _recoveryMovePicker is { } session ? ReadSessionObservation?.Invoke(session.Id)?.Move : null;
    private bool RecoveryBusy => !_recoveryTask.IsCompleted;
    private bool RecoveryDataUnavailable => _recoveryFeed?.Phase == SessionFeedPhase.Live && _recoverySession is
        { Deleted: false, Synchronization: SessionSynchronization.Empty or SessionSynchronization.Hydrating or SessionSynchronization.Stale or SessionSynchronization.Failed };
    private RecoveryTheme RecoveryColors => new(ElevatedColors.Text.Hex, ElevatedColors.Subdued.Hex,
        ElevatedColors.Background.Hex, ElevatedColors.Raise(ElevatedColors.Background).Hex,
        ElevatedColors.Color("text.feedback.warning.default").Hex, ElevatedColors.Color("text.feedback.error.default").Hex,
        ElevatedColors.ActionBackground(ThemeActionVariant.Primary).Hex,
        ElevatedColors.ActionBackground(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        ElevatedColors.ActionText(ThemeActionVariant.Primary).Hex,
        ElevatedColors.ActionText(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        ElevatedColors.ActionText(ThemeActionVariant.Primary, ThemeActionState.Disabled).Hex);

    private void ReadRecoveryState()
    {
        if (_recoveryStopped) return;
        var feed = ReadRecoveryFeed?.Invoke();
        var session = _sessionId is { } id ? ReadSessionObservation?.Invoke(id) : null;
        if (_recoveryTarget != _sessionId || _recoveryTargetLocation != session?.Session?.Location)
        {
            _recoveryTarget = _sessionId;
            _recoveryTargetLocation = session?.Session?.Location;
            _recoveryGeneration++;
            _recoveryWait?.Cancel();
            _recoveryWaitError = null;
        }
        var location = session?.LocationUnavailable;
        if (location?.SessionId != _sessionId || location?.Location != session?.Session?.Location) location = null;
        if (Equals(feed, _recoveryFeed) && ReferenceEquals(session, _recoverySession) && Equals(location, _recoveryLocation)) return;
        _recoveryFeed = feed;
        _recoverySession = session;
        _recoveryLocation = location;
        _dirty = true;
    }

    private Task RetryRecoveryConnection() => RunRecoveryWait(async token =>
    {
        if (RetryRecoveryFeed is null) throw new InvalidOperationException("Retry is unavailable: the shared receiver callback is not connected.");
        await RetryRecoveryFeed(token);
    });

    private Task ReloadSelectedRecoverySession()
    {
        var id = _sessionId;
        return RunRecoveryWait(async token =>
        {
            if (id is null || ReloadRecoverySession is null) throw new InvalidOperationException("Reload is unavailable for this Session.");
            var snapshot = await ReloadRecoverySession(id.Value, token);
            if (snapshot.SessionId != id.Value) throw new InvalidOperationException("Recovery returned a different Session.");
            if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
        });
    }

    private Task MoveRecoveryLocation()
    {
        var evidence = _recoveryLocation;
        if (SubmitRecoveryMove is not null && evidence is not null)
        {
            var session = ReadSessionObservation?.Invoke(evidence.SessionId)?.Session;
            if (session is null) { _recoveryWaitError = "Current Session metadata is unavailable; no move was submitted."; _dirty = true; return Task.CompletedTask; }
            CloseDialog();
            _recoveryMovePicker = session;
            _dirty = true;
            return Task.CompletedTask;
        }
        return RunRecoveryWait(async token =>
        {
            if (evidence is null || ChooseRecoveryDirectory is null)
                throw new InvalidOperationException("Directory switching is unavailable: no typed Session-move callback is connected.");
            await ChooseRecoveryDirectory(evidence, token);
        });
    }

    private void CloseRecoveryDirectory() { _recoveryMovePicker = null; _dirty = true; }
    private void RecoveryMoveAdmitted(SessionMoveSnapshot move)
    {
        if (move.SessionId == _sessionId) _recoveryWaitError = null;
        // No local directory assignment: 204 admitted a control, not a recovered location.
        ReadRecoveryState();
        _dirty = true;
    }

    private Task RunRecoveryWait(Func<CancellationToken, Task> action)
    {
        if (_recoveryStopped || RecoveryBusy) return Task.CompletedTask;
        _recoveryWait?.Dispose();
        _recoveryWait = CancellationTokenSource.CreateLinkedTokenSource(_configurationLifetime.Token);
        var wait = _recoveryWait;
        var generation = _recoveryGeneration;
        _recoveryWaitError = null;
        _dirty = true;
        return _recoveryTask = Run();

        async Task Run()
        {
            try { await action(wait.Token); }
            catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (generation == _recoveryGeneration && !_recoveryStopped) _recoveryWaitError = SessionClientAdapter.Describe(error);
            }
            finally
            {
                if (generation == _recoveryGeneration && !_recoveryStopped) { ReadRecoveryState(); _dirty = true; }
            }
        }
    }

    // Cancel only the explicit user's wait. Automatic reconnect, observers and server execution survive.
    private void CancelRecoveryWait() { _recoveryWait?.Cancel(); _dirty = true; }
    private async Task StopRecoveryAsync()
    {
        if (_recoveryStopped) return;
        _recoveryStopped = true;
#pragma warning disable MA0042 // Cancel this user's waiter synchronously; automatic reconnect and server execution remain independently owned.
        _recoveryWait?.Cancel();
#pragma warning restore MA0042
        await _recoveryTask;
        _recoveryWait?.Dispose();
    }
}
