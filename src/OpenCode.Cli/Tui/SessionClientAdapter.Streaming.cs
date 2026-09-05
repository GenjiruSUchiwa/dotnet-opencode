namespace OpenCode.Cli.Tui;

using System.Net;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using OpenCode.Client;
using OpenCode.Cli.Tui.Forms;
using OpenCode.Cli.Tui.Recovery;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public enum SessionAdmissionPhase { Waiting, Preparing, Sending, Unconfirmed, Pending, Delivered, Cancelled, Rejected, Consumed }
public sealed record SessionAdmissionSnapshot(SessionId SessionId, MessageId Id, SessionAdmissionPhase Phase,
    InboxDeliveryMode Delivery, bool Admitted, string? Error)
{
    public SessionPromptInput? Input { get; init; }
    public bool Unconfirmed { get; init; }
}
public sealed record SessionAdmissionAvailability(bool Allowed, string? Reason, IReadOnlyList<MessageId> Unconfirmed);

public sealed partial class SessionClientAdapter
{
    private sealed class AdmissionRecord(SessionPromptInput input)
    {
        internal readonly SessionPromptInput Input = input;
        internal SessionAdmissionPhase Phase = SessionAdmissionPhase.Waiting;
        internal InboxDeliveryMode Delivery = input.Delivery ?? InboxDeliveryMode.Steer;
        internal bool Admitted;
        internal string? Error;
        internal long FactVersion;
        internal long Attempt;
        internal bool Unconfirmed;
        internal SessionAdmissionSnapshot Snapshot(SessionId session) => new(session, Input.Id!.Value, Phase, Delivery, Admitted, Error) { Input = Input, Unconfirmed = Unconfirmed };
    }
    private sealed record Submission(SessionId SessionId, MessageId InputId)
    {
        internal readonly Guid Key = Guid.NewGuid();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<Guid, Submission> _submissions = [];
    private readonly Dictionary<MessageId, Pending> _pendingRequests = [];

    public SessionAdmissionAvailability CanAdmit(SessionId? sessionId, SessionPromptInput? input = null)
    {
        lock (_gate)
        {
            var entry = sessionId is { } id ? _observed.GetValueOrDefault(id) : null;
            var unknown = entry?.Admissions.Values.Where(item => item.Unconfirmed)
                .Select(item => item.Input.Id!.Value).ToArray() ?? [];
            if (_disposed != 0) return new(false, "The session client is disposed.", unknown);
            if (_feed.Phase != SessionFeedPhase.Live) return new(false, _feed.Error ?? "The event feed is not connected.", unknown);
            if (entry is { Deleted: true }) return new(false, "The Session was deleted.", unknown);
            if (sessionId is not null && entry is null) return new(false, "Hydrate this Session before submitting input.", unknown);
            if (entry is not null && entry.Synchronization != SessionSynchronization.Live && entry.Creation is null)
                return new(false, entry.Error ?? "The Session is synchronizing.", unknown);
            if (entry is not null && BlockingForm(entry.Id)) return new(false, "Answer the pending form before submitting input.", unknown);
            if (entry is not null && input is { Id: null } && entry.Admissions.Values.Count(item => item.Unconfirmed && SamePrompt(item.Input, input)) > 1)
                return new(false, "More than one matching input is unconfirmed. Retry with its original item ID.", unknown);
            return new(true, null, unknown);
        }
    }

    public SessionAdmissionSnapshot? ReadAdmission(SessionId sessionId, MessageId itemId)
    {
        lock (_gate) return _observed.GetValueOrDefault(sessionId)?.Admissions.GetValueOrDefault(itemId)?.Snapshot(sessionId);
    }

    /// <summary>Admit and return without waiting for model idle. Observation stays in the shared receiver.</summary>
    public async Task<SessionAdmissionSnapshot> AdmitPromptAsync(SessionId? origin, SessionPromptInput input, CancellationToken ct = default)
    {
        await foreach (var update in PromptAsync(origin, input, ct))
            if (update.Admitted)
                return ReadAdmission(update.SessionId, update.PromptId)
                    ?? throw new InvalidOperationException("The admitted input has no admission record.");
        throw new InvalidOperationException("Input observation ended without admission confirmation.");
    }

    public Task<SessionAdmissionSnapshot> AdmitPromptAsync(SessionId? origin, PromptInput input, InboxDeliveryMode delivery = InboxDeliveryMode.Steer,
        CancellationToken ct = default) => AdmitPromptAsync(origin,
            new SessionPromptInput(input.Text, Files: input.Files, Agents: input.Agents, Skills: input.Skills, Delivery: delivery), ct);

    public Task<SessionAdmissionSnapshot> RetryAdmissionAsync(SessionId sessionId, MessageId itemId, CancellationToken ct = default)
    {
        SessionPromptInput input;
        lock (_gate) input = _observed.GetValueOrDefault(sessionId)?.Admissions.GetValueOrDefault(itemId)?.Input
            ?? throw new KeyNotFoundException("No captured input exists for this Session/item ID.");
        return AdmitPromptAsync(sessionId, input, ct);
    }

    private async IAsyncEnumerable<SessionResponseSnapshot> PromptForOrigin(SessionId? origin, SessionPromptInput input,
        SessionCreateInput? create, long viewVersion, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input.Text);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        Observation entry;
        Submission submission;
        Pending pending;
        AdmissionRecord record;
        Channel<SessionResponseSnapshot> updates;
        SessionResponseSnapshot initial;
        bool joined;
        long attempt;
        TaskCompletionSource? ownedCreation = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var id = origin ?? create?.Id ?? Schema.SessionId.Create();
            if (input.Id is { } proposed && _pendingRequests.TryGetValue(proposed, out var foreign) && foreign.SessionId != id)
                throw new InvalidOperationException("This input ID belongs to a different Session.");
            entry = ObservationFor(id);
            if (entry.Deleted) throw new InvalidOperationException("The origin Session was deleted.");
            var matches = input.Id is { } requested
                ? entry.Admissions.Values.Where(item => item.Input.Id == requested).ToArray()
                : entry.Admissions.Values.Where(item => item.Unconfirmed && SamePrompt(item.Input, input)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("More than one matching input is unconfirmed. Retry with its original item ID.");
            if (matches.FirstOrDefault() is { } existing)
            {
                record = existing;
                input = existing.Input; // First admission wins; delivery edits never mutate this capture.
            }
            else
            {
                input = input with { Id = input.Id ?? MessageId.Create() };
                record = new(input);
                entry.Admissions.Add(input.Id.Value, record);
            }
            if (origin is null) entry.Creation ??= (create ?? throw new InvalidOperationException("Session creation input is unavailable.")) with { Id = id };
            if (entry.Creation is not null && entry.CreationReady is not { Task.IsCompleted: false })
                entry.CreationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var inputId = input.Id!.Value;
            joined = _pendingRequests.TryGetValue(inputId, out var active);
            if (joined && active!.SessionId != id) throw new InvalidOperationException("This input ID belongs to a different Session.");
            if (active is { Updates.Completed: true } && !record.Admitted) { joined = false; active = null; }
            attempt = joined ? record.Attempt : ++record.Attempt;
            pending = active ?? new(this, id, inputId) { Admitted = record.Admitted,
                Delivered = record.Phase == SessionAdmissionPhase.Delivered };
            pending.Readers++;
            updates = pending.Updates.Subscribe();
            submission = new(id, pending.PromptId);
            _submissions.Add(submission.Key, submission);
            if (!joined) _pendingRequests[pending.PromptId] = pending;
            if (_viewVersion == viewVersion && _sessionId == origin) { _sessionId = id; LastPromptId = pending.PromptId; }
            initial = pending.Snapshot() with { SessionTitle = entry.Session?.Title ?? entry.Creation?.Title };
            PublishObservation(entry);
        }

        var attempted = false;
        var rejected = false;
        var priorUncertainty = record.Unconfirmed;
        try
        {
            // Identity, not fabricated Session metadata. The origin view can retain this ID even
            // if creation/admission fails or navigation occurs while HTTP is outstanding.
            yield return initial;
            if (record.Admitted && record.Phase is SessionAdmissionPhase.Cancelled or SessionAdmissionPhase.Consumed) yield break;
            if (!joined)
            {
            await entry.Admission.WaitAsync(ct);
            try
            {
                lock (_gate) { if (!record.Admitted) record.Phase = SessionAdmissionPhase.Preparing; PublishObservation(entry); }
                await EnsureConnectedAsync(ct);
                SessionCreateInput? creation;
                lock (_gate) creation = entry.Creation;
                if (creation is not null)
                {
                    lock (_gate)
                    {
                        if (entry.CreationReady is not { Task.IsCompleted: false }) entry.CreationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        ownedCreation = entry.CreationReady;
                    }
                    var created = (await RequestAsync(token => _client.CreateAsync(creation, token), "origin session creation", ct)).Data;
                    if (created.Id != entry.Id) throw new InvalidOperationException("Session creation returned a different Session ID.");
                    lock (_gate)
                    {
                        entry.Creation = null;
                        entry.Session = created;
                        entry.CreationReady?.TrySetResult();
                        entry.Deleted = false;
                        _deletedSessions = _deletedSessions.Remove(entry.Id);
                        if (_sessionId == entry.Id && _viewVersion == viewVersion) CurrentSession = created;
                    }
                }
                var observed = await RefreshObservationAsync(entry.Id, ct);
                var info = observed.Session ?? throw new InvalidOperationException(observed.Error ?? "Origin Session could not be loaded.");
                if (observed.Error is not null) throw new InvalidOperationException(observed.Error);
                var readiness = await RequestAsync(token => _readiness(info, token), "origin execution readiness", ct);
                if (readiness.SelectionError is not null || readiness.ExecutionError is not null)
                    throw new InvalidOperationException(readiness.SelectionError ?? readiness.ExecutionError);
                lock (_gate) if (entry.LocationUnavailable?.Location == info.Location) entry.LocationUnavailable = null;
                await RefreshPermissionCapabilitiesAsync(ct);
                lock (_gate)
                {
                    if (_connectionFailure is not null) throw new IOException("The event stream is disconnected. No input was submitted.", _connectionFailure);
                    if (entry.Synchronization != SessionSynchronization.Live) throw new InvalidOperationException("The origin Session has not completed authoritative synchronization.");
                    if (BlockingForm(entry.Id)) throw new InvalidOperationException("Answer the pending form before submitting another prompt.");
                }

                var messages = await ReadMessagesAsync(entry.Id, ct);
                ReconcilePending(pending, observed.Inbox, messages);
                lock (_gate)
                {
                    pending.Admitted |= record.Admitted;
                    if (pending.Admitted) { record.Admitted = true; record.Unconfirmed = false; record.Error = null; }
                }
                if (!pending.Admitted)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        attempted = true;
                        lock (_gate) { record.Phase = SessionAdmissionPhase.Sending; record.Error = null; PublishObservation(entry); }
                        var response = await RequestAsync(token => _client.PromptAsync(entry.Id, input, token), "prompt admission", ct);
                        lock (_gate)
                        {
                            pending.Admitted = record.Admitted = true;
                            record.Unconfirmed = false;
                            if (record.Phase is not (SessionAdmissionPhase.Delivered or SessionAdmissionPhase.Cancelled or SessionAdmissionPhase.Consumed)) record.Phase = SessionAdmissionPhase.Pending;
                            record.FactVersion = ++entry.Version;
                            if (record.Phase == SessionAdmissionPhase.Pending && !entry.Messages.Any(message => message.Id == pending.PromptId))
                            {
                                var item = response.Data with { Delivery = record.Delivery };
                                entry.Inbox = entry.Inbox.Where(previous => previous.Id != item.Id).Append(item).ToImmutableArray();
                                entry.InboxChanges[item.Id] = (entry.Version, item);
                            }
                            record.Error = null;
                            pending.Publish(); PublishObservation(entry);
                        }
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested)
                    {
                        rejected = error is SessionApiException { StatusCode: { } status } && (int)status < 500;
                        if (!rejected)
                        {
                            // A positive echo/read is authoritative. Absence after a transport/5xx
                            // failure is not proof of rejection, and must not mint a replacement ID.
                            try
                            {
                                var inbox = (await RequestAsync(token => _client.InboxAsync(entry.Id, token), "admission reconciliation", ct)).Data;
                                ReconcilePending(pending, inbox, await ReadMessagesAsync(entry.Id, ct));
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception) { }
                        }
                        lock (_gate)
                        {
                            if (!pending.Admitted)
                            {
                                record.Unconfirmed = priorUncertainty || !rejected;
                                record.Phase = record.Unconfirmed ? SessionAdmissionPhase.Unconfirmed : SessionAdmissionPhase.Rejected;
                                record.Error = Describe(error);
                                throw new InvalidOperationException(rejected ? Describe(error)
                                    : $"{Describe(error)} Admission {pending.PromptId} is unconfirmed; retrying the complete same input in this Session reuses that ID.", error);
                            }
                        }
                    }
                }
                else if (!observed.Running && pending.Delivered && !observed.Inbox.Any(item => item.Id == pending.PromptId))
                {
                    // Recovered delivery is enough to avoid another POST. A read model does not
                    // invent a per-input execution outcome when its terminal event was missed.
                    lock (_gate) { pending.ObservationEnded = true; pending.Status = "Idle"; pending.Publish(); pending.Updates.Writer.TryComplete(); }
                }
                lock (_gate) _ = RefreshLocked(entry);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    ownedCreation?.TrySetException(error);
                    _ = ownedCreation?.Task.Exception;
                    record.Error = Describe(error);
                    entry.LocationUnavailable = LocationRecoveryEvidence.From(entry.Id, entry.Session?.Location, error) ?? entry.LocationUnavailable;
                    if (!attempted && !record.Admitted) record.Phase = record.Unconfirmed ? SessionAdmissionPhase.Unconfirmed : SessionAdmissionPhase.Rejected;
                    pending.Updates.Writer.TryComplete(error);
                    PublishObservation(entry);
                }
                throw;
            }
            finally { entry.Admission.Release(); }
            }

            while (true)
            {
                bool reconcile;
                lock (_gate) { reconcile = pending.ReconcileRequested; pending.ReconcileRequested = false; }
                if (reconcile)
                {
                    var inbox = (await RequestAsync(token => _client.InboxAsync(entry.Id, token), "pending input reconciliation", ct)).Data;
                    ReconcilePending(pending, inbox, await ReadMessagesAsync(entry.Id, ct));
                }
                using var idle = _clock.CreateLinkedCancellationTokenSource(ct);
                lock (_gate) if (_feed.Phase == SessionFeedPhase.Live && entry.Synchronization == SessionSynchronization.Live
                    && !BlockingForm(entry.Id) && !BlockingPermission(entry.Id)) idle.CancelAfter(_timeouts.EventIdle);
                bool available;
                try { available = await updates.Reader.WaitToReadAsync(idle.Token); }
                catch (OperationCanceledException error) when (!ct.IsCancellationRequested && idle.IsCancellationRequested)
                { throw new TimeoutException("Session observation timed out. Server execution was not interrupted; reopen or refresh the Session to continue observing.", error); }
                if (!available) break;
                while (updates.Reader.TryRead(out var update)) yield return update;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ownedCreation is { Task.IsCompleted: false }) ownedCreation.TrySetCanceled(ct);
                if (!joined && record.Attempt == attempt && attempted && !rejected && !pending.Admitted && !record.Admitted)
                {
                    record.Phase = SessionAdmissionPhase.Unconfirmed;
                    record.Unconfirmed = true;
                    record.Error ??= $"Admission {pending.PromptId} is unconfirmed; retry the complete same input to reuse its ID.";
                    pending.Updates.Writer.TryComplete(new InvalidOperationException(record.Error));
                    PublishObservation(entry);
                }
                else if (!joined && record.Attempt == attempt && !attempted && !record.Admitted && ct.IsCancellationRequested)
                {
                    record.Phase = record.Unconfirmed ? SessionAdmissionPhase.Unconfirmed : SessionAdmissionPhase.Cancelled;
                    record.Error = record.Unconfirmed ? "Retry cancelled; the original admission is still unconfirmed." : "Local admission was cancelled before sending.";
                    pending.Updates.Writer.TryComplete(new OperationCanceledException(ct));
                    PublishObservation(entry);
                }
                pending.Updates.Remove(updates);
                if (--pending.Readers == 0 && ReferenceEquals(_pendingRequests.GetValueOrDefault(pending.PromptId), pending)) _pendingRequests.Remove(pending.PromptId);
                _submissions.Remove(submission.Key);
                if (entry.CreationReady is { Task.IsCompleted: false } abandoned && !_submissions.Values.Any(item => item.SessionId == entry.Id)) abandoned.TrySetCanceled(ct);
                submission.Finished.TrySetResult();
                // Cancelling a reader is never session.interrupt. The shared observer survives
                // navigation/reader disposal, while adapter disposal cancels only owned I/O.
            }
        }
    }

    private bool BlockingPermission(SessionId id)
    {
        var family = FormDescendants(id).Append(id).ToHashSet();
        return _permissions.Values.Any(permission => family.Contains(permission.SessionId));
    }

    // Forms/Integrations partials may call this under _gate after an HTTP-confirmed cache change.
    // It wakes every item monitor, not just the visible compatibility _pending pointer.
    private void NotifyPendingStateChanged()
    {
        foreach (var entry in _observed.Values) PublishObservation(entry);
        foreach (var pending in _pendingRequests.Values) pending.Publish();
    }

    internal static SessionPromptInput SnapshotPrompt(SessionPromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Text);
        if (input.Id is { } id && (!id.IsInitialized() || !id.Value.StartsWith("msg_", StringComparison.Ordinal)))
            throw new ArgumentException("Prompt ID must be a canonical msg_ identifier.", nameof(input));
        return input with
        {
            Files = input.Files?.ToImmutableArray(), Agents = input.Agents?.ToImmutableArray(), Skills = input.Skills?.ToImmutableArray(),
            Metadata = input.Metadata?.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
        };
    }

    internal static bool SamePrompt(SessionPromptInput left, SessionPromptInput right) =>
        JsonSerializer.Serialize(left with { Id = null, Delivery = left.Delivery ?? InboxDeliveryMode.Steer, Resume = left.Resume ?? true }, SessionProtocolJsonContext.Default.SessionPromptInput)
        == JsonSerializer.Serialize(right with { Id = null, Delivery = right.Delivery ?? InboxDeliveryMode.Steer, Resume = right.Resume ?? true }, SessionProtocolJsonContext.Default.SessionPromptInput);

    private void ReconcileAdmissions(Observation entry, long readVersion)
    {
        foreach (var pair in entry.Admissions)
        {
            var record = pair.Value;
            if (record.Admitted && record.Phase == SessionAdmissionPhase.Cancelled) continue;
            if (entry.Inbox.FirstOrDefault(item => item.Id == pair.Key) is { } pending)
            {
                record.Admitted = true; record.Unconfirmed = false; record.Phase = SessionAdmissionPhase.Pending;
                record.Delivery = pending.Delivery; record.Error = null;
            }
            else if (entry.Messages.Any(message => message.Id == pair.Key && message is UserMessage or SyntheticMessage))
            { record.Admitted = true; record.Unconfirmed = false; record.Phase = SessionAdmissionPhase.Delivered; record.Error = null; }
            else if (record.Admitted && record.Phase == SessionAdmissionPhase.Pending && record.FactVersion <= readVersion)
            {
                record.Phase = SessionAdmissionPhase.Consumed;
                record.Error = "This acknowledged input is no longer pending; its delivery was not confirmed by the read model.";
            }
            if (!_pendingRequests.TryGetValue(pair.Key, out var monitor)) continue;
            monitor.Admitted |= record.Admitted;
            if (record.Phase is SessionAdmissionPhase.Cancelled or SessionAdmissionPhase.Consumed)
            {
                monitor.ObservationEnded = true;
                monitor.Status = record.Phase == SessionAdmissionPhase.Cancelled ? "Cancelled" : "Delivery unconfirmed";
                monitor.Publish(); monitor.Updates.Writer.TryComplete();
            }
        }
    }

    private void ObserveAdmissionEvent(Observation entry, MessageId id, SessionAdmissionPhase? phase = null, InboxDeliveryMode? delivery = null)
    {
        if (!entry.Admissions.TryGetValue(id, out var record)) return;
        if (record.Admitted && record.Phase is SessionAdmissionPhase.Cancelled or SessionAdmissionPhase.Delivered && phase == SessionAdmissionPhase.Pending) return;
        record.Admitted = true; record.Unconfirmed = false; record.Error = null; record.FactVersion = entry.Version;
        if (phase is { } state) record.Phase = state;
        if (delivery is { } mode) record.Delivery = mode;
        if (!_pendingRequests.TryGetValue(id, out var monitor)) return;
        monitor.Admitted = true;
        if (phase == SessionAdmissionPhase.Cancelled)
        {
            monitor.ObservationEnded = true; monitor.Status = "Cancelled";
            monitor.Publish(); monitor.Updates.Writer.TryComplete();
        }
    }

    private bool BlockingForm(SessionId id)
    {
        var info = _observed.GetValueOrDefault(id)?.Session;
        return info is not null && FormAdapter.FirstForRoute(_forms.Values.ToArray(), info.Location, id, FormDescendants(id), info.ParentId is not null) is not null;
    }

    private string PendingStatus(Pending pending)
    {
        lock (_gate)
        {
            if (pending.Outcome is { } outcome) return outcome.ToString().ToLowerInvariant();
            var admission = _observed.GetValueOrDefault(pending.SessionId)?.Admissions.GetValueOrDefault(pending.PromptId);
            if (admission?.Phase == SessionAdmissionPhase.Cancelled) return "Cancelled";
            if (pending.ObservationEnded) return pending.Status;
            if (_feed.Phase != SessionFeedPhase.Live) return "Reconnecting";
            if (_observed.GetValueOrDefault(pending.SessionId)?.Synchronization != SessionSynchronization.Live) return "Synchronizing";
            if (!pending.Admitted) return "Admitting";
            if (BlockingPermission(pending.SessionId)) return "Permission required";
            if (BlockingForm(pending.SessionId)) return "Form response required";
            return pending.Delivered ? "Running" : "Queued";
        }
    }

    /// <summary>Explicit user action only. No observer/disposal path calls this method.</summary>
    public async Task InterruptSessionAsync(SessionId id, CancellationToken ct = default)
    {
        await RequestAsync(async token => { await _client.InterruptAsync(id, continueExecution: false, token); return true; }, "session interruption", ct);
        await RefreshObservationAsync(id, ct);
    }
}
