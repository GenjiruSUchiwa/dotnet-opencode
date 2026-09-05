namespace OpenCode.Cli.Commands.Run;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

/// <summary>One durable admission plus observation of server execution, never an LLM loop.</summary>
internal sealed class RunConversation(SessionHttpClient client, SessionInfo session, RunOptions options, TimeProvider clock)
{
    private readonly MessageId _messageId = MessageId.Create();
    private readonly RunOutput _output = new(options, session.Id, session.Location.Directory, clock);
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _permissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _forms = new(StringComparer.Ordinal);
    private bool _promoted;
    private bool _permissionRejected;
    private bool _formCancelled;
    private bool _finalizing;
    private JsonObject? _prePromotionError;

    public async Task<int> ExecuteAsync(string text, IReadOnlyList<PromptInputFileAttachment> files,
        string? agent, ModelRef? model, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = client.SubscribeEventsAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        await using var eventLifetime = events.ConfigureAwait(true);
        if (!await events.MoveNextAsync().ConfigureAwait(false)) throw new IOException("Event stream disconnected before prompt admission");
        Task? consuming = null;
        try
        {
            if (!string.IsNullOrEmpty(agent)) await client.SwitchAgentAsync(session.Id, AgentId.FromExisting(agent), ct).ConfigureAwait(false);
            if (model is not null) await client.SwitchModelAsync(session.Id, model, ct).ConfigureAwait(false);
            consuming = ConsumeAsync(events, stop.Token);
            // Stable ID supplied once. A lost response is not retried as a new prompt.
            await client.PromptAsync(session.Id, new SessionPromptInput(text, Id: _messageId, Files: files,
                Delivery: InboxDeliveryMode.Steer), ct).ConfigureAwait(false);

            // Source snapshots close the subscription/admission gap. A failed
            // advisory snapshot does not invent an empty authoritative state.
            await Task.WhenAll(SnapshotPermissions(ct), SnapshotForms(ct)).ConfigureAwait(false);
            var waiting = client.WaitAsync(session.Id, ct);
            if (await Task.WhenAny(waiting, consuming).ConfigureAwait(false) == consuming)
                await consuming.ConfigureAwait(false); // A broken stream must not look like success.
            await waiting.ConfigureAwait(false);
            _finalizing = true;
            var projected = await ProjectedAsync(ct).ConfigureAwait(false);
            await _outputGate.WaitAsync(ct).ConfigureAwait(false);
            try { foreach (var message in projected.Messages) _output.Reconcile(message); }
            finally { _outputGate.Release(); }
            if (!projected.Messages.Any(message => message is AssistantMessage)
                && !_permissionRejected && !_formCancelled && !_output.Failed && _prePromotionError is null)
                await consuming.ConfigureAwait(false);
            if (!projected.Found && !_permissionRejected && !_formCancelled && !_output.Failed)
                _output.ExecutionError(_prePromotionError ?? new JsonObject { ["type"] = "unknown", ["message"] = "Prompt was not promoted" }, Now());
            if (consuming.IsFaulted) await consuming.ConfigureAwait(false);
            return _output.Failed ? 1 : 0;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (consuming is not null)
            {
                // The execution/admission path above observes failures; finalization
                // must close the stream without masking the original failure.
                try { await consuming.ConfigureAwait(false); }
                catch (Exception) when (stop.IsCancellationRequested) { }
            }
        }
    }

    private async Task ConsumeAsync(IAsyncEnumerator<ServerEventEnvelope> events, CancellationToken ct)
    {
        while (await events.MoveNextAsync().ConfigureAwait(false))
        {
            var item = events.Current;
            var data = item.Data;
            if (item.Type == "permission.asked" && Session(data) == session.Id.Value)
            {
                await PermissionAsync(data.Deserialize(OpenCodeJsonContext.Default.PermissionRequest)
                    ?? throw new JsonException("Missing permission request."), ct).ConfigureAwait(false);
                continue;
            }
            if (item.Type == "form.created" && data.TryGetProperty("form", out var form) && Session(form) == session.Id.Value)
            {
                await FormAsync(form.Deserialize(OpenCodeJsonContext.Default.FormInfo)
                    ?? throw new JsonException("Missing form request."), ct).ConfigureAwait(false);
                continue;
            }
            // An attached headless client must never answer/cancel unrelated global forms.
            if (Session(data) != session.Id.Value) continue;
            if (item.Type == "session.inbox.delivered" && data.GetProperty("inboxID").GetString() == _messageId.Value)
            { _promoted = true; _prePromotionError = null; continue; }
            if (item.Type == "session.execution.interrupted" && data.GetProperty("reason").GetString() == "user"
                && (_permissionRejected || _formCancelled)) return;
            if (!_promoted)
            {
                if (item.Type == "session.execution.failed")
                {
                    _prePromotionError = JsonNode.Parse(data.GetProperty("error").GetRawText())!.AsObject();
                    if (_finalizing) return;
                }
                if (_finalizing && item.Type is "session.execution.succeeded" or "session.execution.interrupted") return;
                continue;
            }
            if (_finalizing && !item.Type.StartsWith("session.execution.", StringComparison.Ordinal)) continue;
            await _outputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (item.Type == "session.step.failed" && (_permissionRejected || _formCancelled)) continue;
                if (item.Type == "session.execution.failed")
                {
                    if (!_output.Failed && !_formCancelled)
                        _output.ExecutionError(JsonNode.Parse(data.GetProperty("error").GetRawText())!.AsObject(), item.Created ?? Now());
                    return;
                }
                if (item.Type == "session.execution.interrupted")
                {
                    if (data.GetProperty("reason").GetString() != "user" && !_output.Failed)
                        _output.ExecutionError(new JsonObject { ["type"] = "aborted", ["message"] = "Session interrupted: " + data.GetProperty("reason").GetString() }, item.Created ?? Now());
                    return;
                }
                if (item.Type == "session.execution.succeeded") return;
                _output.Event(item);
            }
            finally { _outputGate.Release(); }
        }
        if (!_output.Failed) throw new IOException("Event stream disconnected during prompt execution");
    }

    private async Task PermissionAsync(PermissionRequest request, CancellationToken ct)
    {
        if (request.SessionId != session.Id || !_permissions.TryAdd(request.Id.Value, 0)) return;
        if (!options.Auto)
        {
            _permissionRejected = true;
            Console.Error.WriteLine($"\x1b[93m\x1b[1m! \x1b[0mpermission requested: {request.Action} ({string.Join(", ", request.Resources)}); auto-rejecting");
        }
        // Source deliberately tolerates replies that raced another responder.
        try { await client.ReplyPermissionAsync(session.Id, request.Id, options.Auto ? PermissionReply.Once : PermissionReply.Reject, ct: ct).ConfigureAwait(false); }
        catch (HttpRequestException) { }
        if (!options.Auto)
        {
            try { await client.InterruptAsync(session.Id, ct: ct).ConfigureAwait(false); }
            catch (HttpRequestException) { }
        }
    }
    private async Task FormAsync(FormInfo form, CancellationToken ct)
    {
        if (form.SessionId != session.Id.Value || !_forms.TryAdd(form.Id.Value, 0)) return;
        try { await client.CancelFormAsync(form.SessionId, form.Id, ct: ct).ConfigureAwait(false); }
        catch (SessionApiException error) when (error.Payload is { } payload && payload.TryGetProperty("_tag", out var tag) && tag.GetString() == "FormAlreadySettledError") { }
        _formCancelled = true;
    }
    private async Task SnapshotPermissions(CancellationToken ct)
    {
        IReadOnlyList<PermissionRequest> requests;
        try { requests = (await client.ListSessionPermissionsAsync(session.Id, ct).ConfigureAwait(false)).Data; }
        catch (HttpRequestException) { return; }
        foreach (var request in requests) await PermissionAsync(request, ct).ConfigureAwait(false);
    }
    private async Task SnapshotForms(CancellationToken ct)
    {
        IReadOnlyList<FormInfo> forms;
        try { forms = (await client.ListFormsAsync(session.Id.Value, ct: ct).ConfigureAwait(false)).Data; }
        catch (HttpRequestException) { return; }
        foreach (var form in forms) await FormAsync(form, ct).ConfigureAwait(false);
    }
    private async Task<(bool Found, IReadOnlyList<SessionMessage> Messages)> ProjectedAsync(CancellationToken ct)
    {
        var rows = new List<SessionMessage>();
        string? cursor = null;
        do
        {
            var page = await client.MessagesAsync(session.Id, cursor is null ? new(200, SessionOrder.Descending)
                : SessionMessagesQuery.FromCursor(cursor, 200), ct).ConfigureAwait(false);
            foreach (var message in page.Data)
            {
                if (message.Id == _messageId) { rows.Reverse(); return (true, rows); }
                rows.Add(message);
            }
            cursor = page.Cursor.Next;
        } while (cursor is not null);
        return (false, []);
    }
    private static string? Session(JsonElement data) => data.TryGetProperty("sessionID", out var id) ? id.GetString() : null;
    private double Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
}
