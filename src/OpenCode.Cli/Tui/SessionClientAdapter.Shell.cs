namespace OpenCode.Cli.Tui;

using OpenCode.Cli.Tui.Components;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionClientAdapter
{
    public async Task ExecuteObservedShellAsync(SessionShellSubmission submission, Func<SessionInfo, Task> sessionReady,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        var session = submission.Session is { } existing
            ? (await RequestAsync(token => _client.GetAsync(existing, token), "shell Session lookup", cancellationToken)).Data
            : (await RequestAsync(token => _client.CreateAsync(new SessionCreateInput(Location: submission.Location,
                Agent: submission.Agent?.Value, Model: submission.Model), token), "shell Session creation", cancellationToken)).Data;
        var snapshot = await ObserveSessionAsync(session.Id, cancellationToken);
        if (snapshot.Deleted || snapshot.Error is not null)
            throw new InvalidOperationException(snapshot.Error ?? "The shell Session was deleted.");
        await sessionReady(session); // Make pending permission requests reachable before POST.
        Observation observed;
        lock (_gate) observed = ObservationFor(session.Id);
        await observed.Admission.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
                if (observed.NeedsReconciliation || observed.Deleted)
                    throw new InvalidOperationException("Reconcile the Session before running a shell command.");
            // No lookup timeout, automatic retry, prompt inbox ID, or local completion message.
            await _client.RunSessionShellAsync(session.Id, submission.Command, ct: cancellationToken);
        }
        catch
        {
            lock (_gate)
            {
                observed.NeedsReconciliation = true;
                observed.Invalidated = true;
                observed.Error = "Shell request outcome is unknown. Reload the Session before submitting again.";
                PublishObservation(observed);
            }
            throw;
        }
        finally { observed.Admission.Release(); }
        // A failed read after an acknowledged POST must not restore a runnable draft.
        try { await RefreshObservationAsync(session.Id, cancellationToken); }
        catch (Exception exception)
        {
            lock (_gate)
            {
                observed.Invalidated = true;
                observed.Error = "Shell request completed, but Session refresh failed: " + Describe(exception);
                PublishObservation(observed);
            }
        }
    }
}
