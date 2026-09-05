namespace OpenCode.Core.Forms;

using OpenCode.Schema;
using OpenCode.Core.Mcp;

public sealed class FormNotFoundException(FormId id) : Exception($"Form not found: {id}") { public FormId Id { get; } = id; }
public sealed class FormAlreadySettledException(FormId id) : Exception($"Form already settled: {id}") { public FormId Id { get; } = id; }
public sealed class FormAlreadyExistsException(FormId id) : Exception($"Form already exists: {id}") { public FormId Id { get; } = id; }
public sealed class FormInvalidAnswerException(FormId id, string message) : Exception(message) { public FormId Id { get; } = id; }
public sealed class FormInvalidException(string message) : Exception(message);

/// <summary>One Location's ephemeral forms. Publication precedes settlement; no database or global registry.</summary>
public sealed class FormService(LocationRef location, Action<OpenCodeEvent> publish, TimeProvider? clock = null) : IDisposable, IMcpElicitationForms
{
    private sealed class Entry(FormInfo form)
    {
        public FormInfo Form { get; } = form;
        public FormState State = new FormPendingState();
        public DateTimeOffset? Expires;
        public TaskCompletionSource<FormState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<FormId, Entry> _forms = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _closed;

    public FormInfo Create(string owner, FormCreatePayload input)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            Prune();
            var id = input.Id ?? FormId.Create();
            if (_forms.ContainsKey(id)) throw new FormAlreadyExistsException(id);
            if (FormValidation.Fields(input.Fields) is { } invalid) throw new FormInvalidException(invalid);
            var form = new FormInfo(id, owner, input.Title, input.Fields, input.Metadata);
            _forms.Add(id, new Entry(form));
            try { Publish(FormEventDefinitions.Created, new FormCreatedEventData(form)); }
            catch { _forms.Remove(id); throw; }
            return form;
        }
    }

    /// <summary>Used by MCP form/URL elicitation adapters; cancellation cancels the pending form, never approves it.</summary>
    public Task<FormState> AskAsync(FormInfo form, CancellationToken ct) =>
        AskAsync(form.SessionId, new FormCreatePayload(form.Title, form.Fields, form.Id, form.Metadata), ct);

    public Task<bool> TryReplyAsync(FormId id, FormAnswer answer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { Reply(id, answer); return Task.FromResult(true); }
        catch (FormNotFoundException) { return Task.FromResult(false); }
        catch (FormAlreadySettledException) { return Task.FromResult(false); }
    }

    /// <summary>Used by MCP form/URL elicitation adapters; cancellation cancels the pending form, never approves it.</summary>
    public async Task<FormState> AskAsync(string owner, FormCreatePayload input, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        FormInfo form;
        Task<FormState> completion;
        lock (_gate)
        {
            form = Create(owner, input);
            completion = Require(form.Id).Completion.Task;
        }
        try { return await completion.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { Cancel(form.Id); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Could not cancel interrupted form {0}: {1}", form.Id, error.Message); }
            throw;
        }
    }

    public FormInfo Get(FormId id) { lock (_gate) return Require(id).Form; }
    public FormState State(FormId id) { lock (_gate) return Require(id).State; }
    public IReadOnlyList<FormInfo> List(string? owner = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            Prune();
            return _forms.Values.Where(entry => entry.State is FormPendingState && (owner is null || entry.Form.SessionId == owner))
                .Select(entry => entry.Form).ToArray();
        }
    }

    public void Reply(FormId id, FormAnswer answer)
    {
        lock (_gate)
        {
            var entry = Require(id);
            if (entry.State is not FormPendingState) throw new FormAlreadySettledException(id);
            if (FormValidation.Answer(entry.Form.Fields, answer) is { } invalid) throw new FormInvalidAnswerException(id, invalid);
            Publish(FormEventDefinitions.Replied, new FormRepliedEventData(id, entry.Form.SessionId, answer));
            Settle(entry, new FormAnsweredState(answer));
        }
    }

    public void Cancel(FormId id)
    {
        lock (_gate)
        {
            var entry = Require(id);
            if (entry.State is not FormPendingState) throw new FormAlreadySettledException(id);
            Publish(FormEventDefinitions.Cancelled, new FormCancelledEventData(id, entry.Form.SessionId));
            Settle(entry, new FormCancelledState());
        }
    }

    private Entry Require(FormId id)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        Prune();
        return _forms.TryGetValue(id, out var entry) ? entry : throw new FormNotFoundException(id);
    }

    private void Prune()
    {
        foreach (var id in _forms.Where(pair => pair.Value.Expires <= _clock.GetUtcNow()).Select(pair => pair.Key).ToArray()) _forms.Remove(id);
    }

    private void Settle(Entry entry, FormState state)
    {
        entry.State = state;
        entry.Expires = _clock.GetUtcNow().AddMinutes(10);
        entry.Completion.TrySetResult(state);
    }

    private void Publish<T>(EphemeralEventDefinition<T> definition, T data) where T : class =>
        publish(definition.Create(EventId.Create(), _clock.GetUtcNow().ToUnixTimeMilliseconds(), data, location));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            foreach (var entry in _forms.Values.Where(entry => entry.State is FormPendingState).ToArray())
            {
                try { Cancel(entry.Form.Id); }
                catch (Exception error)
                {
                    entry.Completion.TrySetException(error);
                    System.Diagnostics.Trace.TraceWarning("Could not publish form cancellation: {0}", error.Message);
                }
            }
            _closed = true;
            _forms.Clear();
        }
    }
}
