namespace OpenCode.Cli.Tui;

using OpenCode.Cli.Tui.Commands;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionClientAdapter
{
    /// <summary>Commands return HTTP 204, not an inbox identity. Observe the Session before admission and refresh that same observer afterwards.</summary>
    public async Task<SessionInfo> ExecuteObservedCommandAsync(CommandSubmission submission, Func<SessionInfo, Task> sessionReady,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        var session = submission.Session is { } existing
            ? (await RequestAsync(token => _client.GetAsync(existing, token), "command session lookup", cancellationToken)).Data
            : (await RequestAsync(token => _client.CreateAsync(new SessionCreateInput(Location: submission.Location,
                Agent: submission.Agent?.Value, Model: submission.Model), token), "command session creation", cancellationToken)).Data;
        var snapshot = await ObserveSessionAsync(session.Id, cancellationToken);
        if (snapshot.Deleted || snapshot.Error is not null)
            throw new InvalidOperationException(snapshot.Error ?? "The command session was deleted.");
        // Interpolation may request permission before the HTTP command returns.
        // Make the real Session reachable in its origin view before starting that work.
        await sessionReady(session);
        Observation observed;
        lock (_gate) observed = ObservationFor(session.Id);
        await observed.Admission.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
                if (observed.NeedsReconciliation || observed.Deleted)
                    throw new InvalidOperationException("Reconcile the session before admitting a command.");
            // This HTTP call may wait for an explicit permission reply during
            // interpolation. Do not impose the ordinary short lookup timeout.
            await _client.ExecuteCommandAsync(session.Id, submission.Command, submission.Prompt, submission.Delivery, cancellationToken);
            // All content/events flow through the existing process-wide receiver and
            // Session read model. Refresh is not a second subscription or model loop.
            await RefreshObservationAsync(session.Id, cancellationToken);
            return session;
        }
        finally { observed.Admission.Release(); }
    }
}
