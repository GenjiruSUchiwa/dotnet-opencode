namespace OpenCode.Core.Session;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.ExceptionServices;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Llm;
using OpenCode.Core.Permissions;
using OpenCode.Core.Tools;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Core.Snapshot;
using OpenCode.Schema;

/// <summary>One physical stream and its durable settlement, not a model/tool execution loop.</summary>
internal sealed class SessionAttempt(SessionStore store, SessionId sessionId, ResolvedModel model, string agent, Action<string> output,
    ToolSnapshot? snapshot, SessionToolOutput outputPolicy, MessageId assistantId, SessionRetry retry,
    IReadOnlyList<CatalogCost>? costs = null, Func<CancellationToken, Task<bool>>? recoverOverflow = null, SnapshotService? snapshots = null,
    bool recoverContinuation = true)
{
    private readonly MessageId _assistantId = assistantId;
    private sealed class Fragment(string id, int ordinal)
    {
        internal readonly string Id = id;
        internal readonly int Ordinal = ordinal;
        internal readonly StringBuilder Text = new();
        internal JsonObject? State;
    }
    private sealed class Tool(string name, bool hosted)
    {
        internal readonly string Name = name;
        internal readonly StringBuilder Input = new();
        internal bool InputEnded;
        internal bool Called;
        internal bool Settled;
        internal bool Hosted = hosted;
        internal JsonObject? Progress;
    }
    private Fragment? _text;
    private Fragment? _reasoning;
    private int _textOrdinal;
    private int _reasoningOrdinal;
    private readonly Dictionary<string, Tool> _tools = [];
    private LlmEvent.StepFinish? _finish;
    private SessionStructuredError? _failure;
    private bool _started;
    private bool _outputStarted;
    private bool _providerFailed;
    private readonly List<Task<Exception?>> _toolRuns = [];
    private CancellationToken _work;
    private bool _toolsAllowed;
    private SnapshotId? _startSnapshot;

    internal async Task<SessionAttemptOutcome> RunAsync(LlmRequest request, CancellationToken ct)
    {
        _startSnapshot = snapshots is null ? null : await snapshots.CaptureAsync(ct).ConfigureAwait(false);
        using var toolWork = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _work = toolWork.Token;
        _toolsAllowed = snapshot is not null && request.ToolChoice is not LlmToolChoice.None;
        Exception? failure = null;
        LlmException? overflowFailure = null;
        try
        {
            try
            {
                // Exactly one physical request; no SDK/in-memory tool loop.
                var terminal = false;
                await foreach (var item in model.Client.StreamAsync(request, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    terminal |= item is LlmEvent.Finish or LlmEvent.ProviderError;
                    // Drain the response after an emitted error, but do not publish subsequent provider content.
                    if (overflowFailure is not null || _providerFailed) continue;
                    if (!_outputStarted && item is LlmEvent.ProviderError { Reason: LlmFailure.InvalidRequest { Classification: LlmFailureClassification.ContextOverflow } } overflow)
                    {
                        overflowFailure = new LlmException(overflow.Reason);
                        continue;
                    }
                    using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
                    await PublishAsync(item, publication.Token).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                if (overflowFailure is not null) failure = overflowFailure;
                else if (!terminal) throw new LlmException(new LlmFailure.InvalidProviderOutput("The provider response ended unexpectedly.", true));
                else if (_finish is null && !_providerFailed) throw new LlmException(new LlmFailure.InvalidProviderOutput("Provider stream ended without step finish.", true));
            }
            catch (Exception error)
            {
                failure = error;
                // A failed provider body does not cancel already confirmed local calls.
                // Join their durable results before deciding whether context can continue.
                if (error is OperationCanceledException) await toolWork.CancelAsync().ConfigureAwait(false);
            }
            using (var publication = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock))
            {
                await FlushAsync(publication.Token).ConfigureAwait(false);
                if (_started && overflowFailure is null) await AppendAsync("session.step.streamed", new(), publication.Token).ConfigureAwait(false);
            }
            // A permission ask may legitimately outlive the provider body. Do not
            // run this join under a 15-second publication/cleanup timeout.
            var toolFailures = await Task.WhenAll(_toolRuns).ConfigureAwait(false);
            failure ??= toolFailures.FirstOrDefault(error => error is not null);
            if (failure is null && ct.IsCancellationRequested) failure = new OperationCanceledException(ct);
            if (!_outputStarted && !ct.IsCancellationRequested && recoverOverflow is not null &&
                (overflowFailure ?? failure) is LlmException { Reason: LlmFailure.InvalidRequest { Classification: LlmFailureClassification.ContextOverflow } } &&
                await recoverOverflow(ct).ConfigureAwait(false)) return new SessionAttemptOutcome.Compacted();
            if (overflowFailure is not null)
            {
                _providerFailed = true;
                _failure ??= new SessionStructuredError("provider.unknown", overflowFailure.Reason.Message);
                failure ??= overflowFailure;
            }
            if (failure is null && _finish?.Reason.Normalized == OpenCode.Core.Llm.LlmFinishReason.Unknown)
                failure = new LlmException(new LlmFailure.InvalidProviderOutput("The provider response ended with an unknown finish reason.", true));
            // The transport has rejected its continuation. Reload full projected context once,
            // retaining the logical step, assistant ID, and generic retry budget.
            if (recoverContinuation && !_outputStarted && !ct.IsCancellationRequested &&
                failure is LlmException { Reason: LlmFailure.Transport { Recovery: LlmTransportRecovery.RetryFull or LlmTransportRecovery.RotateAndRetryFull } })
                return new SessionAttemptOutcome.RecoverFull();
            var decision = !_providerFailed && !ct.IsCancellationRequested && toolFailures.All(error => error is null) &&
                failure is LlmException llm
                ? retry.Decide(llm, interruptedStream: _outputStarted && IsInterruptedStream(llm)) : null;
            if (!_outputStarted && decision is not null)
            {
                using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
                await StartAsync(publication.Token).ConfigureAwait(false);
                return new SessionAttemptOutcome.Retry(SessionFailure.From(failure!), decision);
            }
            if (failure is not null)
                _failure ??= SessionFailure.From(failure);
            if (_finish?.Reason.Normalized is OpenCode.Core.Llm.LlmFinishReason.Unknown or OpenCode.Core.Llm.LlmFinishReason.Error)
                _failure ??= new SessionStructuredError("provider.invalid-output", "The provider did not produce a supported complete step outcome.");
            var interrupted = failure is OperationCanceledException || toolFailures.Any(error =>
                error is OperationCanceledException or PermissionDeclinedException or QuestionCancelledException);
            if (interrupted) _failure = new SessionStructuredError("aborted", "Step interrupted");
            var unsettled = interrupted || _providerFailed
                ? new SessionStructuredError("aborted", "Tool execution interrupted")
                : toolFailures.FirstOrDefault(error => error is not null) is { } toolFailure
                    ? SessionFailure.From(toolFailure)
                    : new SessionStructuredError("tool.result-missing", "Provider did not return a tool result");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
            foreach (var pair in _tools.Where(pair => !pair.Value.Settled))
                await FailToolAsync(pair.Key, pair.Value, unsettled, cleanup.Token).ConfigureAwait(false);
            if (_finish is not null || _failure is not null)
            {
                await StartAsync(cleanup.Token).ConfigureAwait(false);
                var terminal = new JsonObject();
                if (snapshots is not null)
                {
                    var end = await snapshots.CaptureAsync(cleanup.Token).ConfigureAwait(false);
                    if (end is { } captured) terminal["snapshot"] = captured.Value;
                    if (_startSnapshot is { } start && end is { } finish)
                    {
                        try { terminal["files"] = JsonSerializer.SerializeToNode(start == finish ? [] : await snapshots.FilesAsync(start, finish, cleanup.Token).ConfigureAwait(false)); }
                        catch (SnapshotException) { } // Snapshot.files is best-effort in the source Step boundary.
                    }
                }
                if (_finish is not null)
                {
                    terminal["rawFinish"] = _finish.Reason.Raw;
                    if (_finish.Reason.Raw is null) terminal.Remove("rawFinish");
                    if (State(_finish) is { } state) terminal["providerState"] = state;
                    var usage = SessionUsage.Tokens(_finish.Usage);
                    terminal["tokens"] = JsonSerializer.SerializeToNode(usage, OpenCodeJsonContext.Default.TokenUsageInfo);
                    terminal["cost"] = SessionUsage.Cost(costs ?? [], usage).Amount;
                }
                if (_failure is not null)
                {
                    terminal["error"] = JsonSerializer.SerializeToNode(_failure, OpenCodeJsonContext.Default.SessionStructuredError);
                    if (_finish?.Reason.Normalized == OpenCode.Core.Llm.LlmFinishReason.ContentFilter) terminal["finish"] = "content-filter";
                    await AppendAsync("session.step.failed", terminal, cleanup.Token).ConfigureAwait(false);
                }
                else
                {
                    terminal["finish"] = Finish(_finish!.Reason.Normalized);
                    await AppendAsync("session.step.ended", terminal, cleanup.Token).ConfigureAwait(false);
                }
            }
            if (_outputStarted && decision is not null)
                return new SessionAttemptOutcome.Continue(_failure!, decision);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (_failure is not null) throw new SessionStepFailedException(_failure);
            return new SessionAttemptOutcome.Completed(_toolsAllowed && _tools.Values.Any(tool => !tool.Hosted && (tool.Called || tool.Settled)));
        }
        finally
        {
            try { await toolWork.CancelAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(_toolRuns).ConfigureAwait(false); }
        }
    }

    private async Task PublishAsync(LlmEvent item, CancellationToken ct)
    {
        if (item is LlmEvent.TextStart or LlmEvent.ReasoningStart or LlmEvent.ToolInputStart or LlmEvent.ToolInputError or LlmEvent.ToolCall)
            _outputStarted = true;
        switch (item)
        {
            case LlmEvent.StepStart start:
                if (start.Index != 0) throw new NotSupportedException("The native attempt accepts one provider step.");
                await StartAsync(ct).ConfigureAwait(false);
                break;
            case LlmEvent.TextStart text:
                if (_text is not null) throw new JsonException("Overlapping text fragments.");
                await StartAsync(ct).ConfigureAwait(false);
                _text = new Fragment(text.Id, _textOrdinal++) { State = State(item) };
                await AppendAsync("session.text.started", new() { ["ordinal"] = _text.Ordinal }, ct).ConfigureAwait(false);
                break;
            case LlmEvent.TextDelta text:
                Require(_text, text.Id).Text.Append(text.Text);
                Merge(_text!, State(item));
                SessionEvents.ContentDelta(sessionId, _assistantId, _text!.Ordinal, text.Text, false, store.Clock);
                break;
            case LlmEvent.TextEnd text:
                Require(_text, text.Id);
                await EndAsync(false, text.Text, State(item), ct).ConfigureAwait(false);
                break;
            case LlmEvent.ReasoningStart reasoning:
                if (_reasoning is not null) throw new JsonException("Overlapping reasoning fragments.");
                await StartAsync(ct).ConfigureAwait(false);
                _reasoning = new Fragment(reasoning.Id, _reasoningOrdinal++) { State = State(item) };
                var started = new JsonObject { ["ordinal"] = _reasoning.Ordinal };
                if (_reasoning.State is not null) started["state"] = _reasoning.State.DeepClone();
                await AppendAsync("session.reasoning.started", started, ct).ConfigureAwait(false);
                break;
            case LlmEvent.ReasoningDelta reasoning:
                Require(_reasoning, reasoning.Id).Text.Append(reasoning.Text);
                Merge(_reasoning!, State(item));
                SessionEvents.ContentDelta(sessionId, _assistantId, _reasoning!.Ordinal, reasoning.Text, true, store.Clock);
                break;
            case LlmEvent.ReasoningEnd reasoning:
                Require(_reasoning, reasoning.Id);
                await EndAsync(true, reasoning.Text, State(item), ct).ConfigureAwait(false);
                break;
            case LlmEvent.ToolInputStart tool: await StartToolAsync(tool.Id, tool.Name, tool.ProviderExecuted, ct).ConfigureAwait(false); break;
            case LlmEvent.ToolInputDelta tool:
                var writing = GetTool(tool.Id, tool.Name);
                if (writing.InputEnded) throw new JsonException("Tool input delta after end.");
                writing.Input.Append(tool.Text);
                SessionEvents.ToolInputDelta(sessionId, _assistantId, tool.Id, tool.Text, store.Clock);
                break;
            case LlmEvent.ToolInputEnd tool: await EndToolAsync(tool.Id, GetTool(tool.Id, tool.Name), ct).ConfigureAwait(false); break;
            case LlmEvent.ToolCall tool:
                if (!_tools.ContainsKey(tool.Id)) await StartToolAsync(tool.Id, tool.Name, tool.ProviderExecuted, ct).ConfigureAwait(false);
                var called = GetTool(tool.Id, tool.Name);
                if (called.Called || called.Settled) throw new JsonException("Duplicate or settled tool call.");
                if (!called.InputEnded) await EndToolAsync(tool.Id, called, ct).ConfigureAwait(false);
                var call = new JsonObject
                {
                    ["id"] = tool.Id, ["executed"] = tool.ProviderExecuted,
                    // Durable Tool.Called requires a record; execution still validates the original input.
                    ["input"] = tool.Input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(tool.Input.GetRawText())
                        : new JsonObject { ["value"] = JsonNode.Parse(tool.Input.GetRawText()) }
                };
                if (State(item) is { } state) call["state"] = state;
                await AppendAsync("session.tool.called", call, ct).ConfigureAwait(false);
                called.Called = true;
                called.Hosted = tool.ProviderExecuted;
                if (!called.Hosted)
                {
                    if (!_toolsAllowed)
                        await FailToolAsync(tool.Id, called, new SessionStructuredError("tool.execution", "Tools are disabled for this request."), ct).ConfigureAwait(false);
                    else _toolRuns.Add(ExecuteToolAsync(tool.Id, called, tool.Input.Clone()));
                }
                break;
            case LlmEvent.ToolInputError tool:
                if (!_tools.ContainsKey(tool.Id)) await StartToolAsync(tool.Id, tool.Name, false, ct).ConfigureAwait(false);
                var malformed = GetTool(tool.Id, tool.Name);
                if (malformed.Called || malformed.Settled) throw new JsonException("Malformed input after tool settlement.");
                malformed.Input.Clear().Append(tool.Raw);
                if (!malformed.InputEnded) await EndToolAsync(tool.Id, malformed, ct).ConfigureAwait(false);
                var malformedError = new SessionStructuredError("tool.input-json", "Tool arguments were malformed JSON and were not executed.");
                await FailToolAsync(tool.Id, malformed, malformedError, ct).ConfigureAwait(false);
                break;
            case LlmEvent.ToolResult result:
                var hosted = GetTool(result.Id, result.Name);
                if (!hosted.Called || !hosted.Hosted) throw new JsonException("Provider result does not belong to a confirmed hosted call.");
                if (hosted.Settled)
                {
                    if (result.Result is LlmToolResult.Error) break;
                    throw new JsonException("Duplicate hosted result.");
                }
                if (result.Result is LlmToolResult.Error hostedError)
                    await FailToolAsync(result.Id, hosted, new SessionStructuredError("tool.execution", ValueText(hostedError.Value)), ct, State(result)).ConfigureAwait(false);
                else
                    await SuccessAsync(result.Id, hosted, HostedContent(result.Result), null, ct, State(result)).ConfigureAwait(false);
                break;
            case LlmEvent.ToolError error:
                var failed = GetTool(error.Id, error.Name);
                if (!failed.Called || !failed.Hosted || failed.Settled) throw new JsonException("Provider error does not belong to a running hosted call.");
                await FailToolAsync(error.Id, failed, new SessionStructuredError(
                    error.Message == $"Unknown tool: {error.Name}" ? "tool.unknown" : "tool.execution", error.Message), ct, State(error)).ConfigureAwait(false);
                break;
            case LlmEvent.StepFinish finish:
                if (_finish is not null || finish.Index != 0) throw new JsonException("Duplicate provider step finish.");
                await FlushAsync(ct).ConfigureAwait(false);
                // Validate metadata before accepting settlement, so failures can be durably recorded.
                State(item);
                _finish = finish;
                if (finish.Reason.Normalized == OpenCode.Core.Llm.LlmFinishReason.ContentFilter)
                {
                    _providerFailed = true;
                    _failure ??= new SessionStructuredError("provider.content-filter", "Provider blocked the response");
                }
                break;
            case LlmEvent.Finish: break;
            case LlmEvent.ProviderError error:
                _providerFailed = true;
                _failure ??= new SessionStructuredError("provider.unknown", error.Reason.Message);
                await StartAsync(ct).ConfigureAwait(false);
                break;
            case LlmEvent.ProviderState:
                throw new NotSupportedException("Opaque provider output has no canonical projector in this runner; it is not silently discarded.");
            default: throw new NotSupportedException("Unsupported structured provider event.");
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        var data = new JsonObject
        {
            ["agent"] = agent, ["model"] = JsonSerializer.SerializeToNode(model.Selection, OpenCodeJsonContext.Default.ModelRef)
        };
        if (_startSnapshot is { } snapshot) data["snapshot"] = snapshot.Value;
        await AppendAsync("session.step.started", data, ct).ConfigureAwait(false);
        _started = true;
    }

    private async Task EndAsync(bool reasoning, string? authoritative, JsonObject? state, CancellationToken ct)
    {
        var fragment = (reasoning ? _reasoning : _text)!;
        Merge(fragment, state);
        var text = authoritative ?? fragment.Text.ToString();
        var data = new JsonObject { ["ordinal"] = fragment.Ordinal, ["text"] = text };
        if (fragment.State is not null) data["state"] = fragment.State.DeepClone();
        await AppendAsync(reasoning ? "session.reasoning.ended" : "session.text.ended", data, ct).ConfigureAwait(false);
        if (reasoning) _reasoning = null;
        else
        {
            _text = null;
            // The legacy string stream cannot retract deltas. Emit durable complete
            // blocks so authoritative TextEnd replacements remain correct.
            output(text);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_text is not null) await EndAsync(false, null, null, ct).ConfigureAwait(false);
        if (_reasoning is not null) await EndAsync(true, null, null, ct).ConfigureAwait(false);
        foreach (var pair in _tools.Where(pair => !pair.Value.InputEnded)) await EndToolAsync(pair.Key, pair.Value, ct).ConfigureAwait(false);
    }

    private async Task StartToolAsync(string id, string name, bool hosted, CancellationToken ct)
    {
        if (_tools.ContainsKey(id)) throw new JsonException("Duplicate tool input start.");
        await StartAsync(ct).ConfigureAwait(false);
        await AppendAsync("session.tool.input.started", new() { ["id"] = id, ["name"] = name }, ct).ConfigureAwait(false);
        _tools.Add(id, new Tool(name, hosted));
    }

    private async Task EndToolAsync(string id, Tool tool, CancellationToken ct)
    {
        if (tool.InputEnded) throw new JsonException("Duplicate tool input end.");
        await AppendAsync("session.tool.input.ended", new() { ["id"] = id, ["text"] = tool.Input.ToString() }, ct).ConfigureAwait(false);
        tool.InputEnded = true;
    }

    private async Task FailToolAsync(string id, Tool tool, SessionStructuredError error, CancellationToken ct, JsonObject? resultState = null)
    {
        var data = new JsonObject
        {
            ["id"] = id, ["executed"] = tool.Hosted,
            ["error"] = JsonSerializer.SerializeToNode(error, OpenCodeJsonContext.Default.SessionStructuredError)
        };
        if (tool.Progress is not null) data["metadata"] = tool.Progress.DeepClone();
        if (resultState is not null) data["resultState"] = resultState;
        await AppendAsync("session.tool.failed", data, ct).ConfigureAwait(false);
        tool.Settled = true;
    }

    private async Task<Exception?> ExecuteToolAsync(string id, Tool tool, JsonElement input)
    {
        try
        {
            var context = new ToolContext(sessionId, AgentId.FromExisting(agent), _assistantId, id, metadata =>
            {
                if (tool.Settled) throw new ToolContractException("Tool progress after settlement.");
                tool.Progress = JsonSerializer.SerializeToNode(metadata)!.AsObject();
                SessionEvents.ToolProgress(sessionId, _assistantId, id, tool.Progress, store.Clock);
                return Task.CompletedTask;
            });
            ToolExecutionResult result;
            try { result = await snapshot!.ExecuteAsync(tool.Name, input, context, _work).ConfigureAwait(false); }
            catch (ToolExecutionException error)
            {
                using var failure = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
                await FailToolAsync(id, tool, SessionFailure.From(error), failure.Token).ConfigureAwait(false);
                return null;
            }
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
            // A confirmed tool result outlives work cancellation, including truncation and publication.
            result = await outputPolicy.TruncateAsync(result, completion.Token).ConfigureAwait(false);
            await SuccessAsync(id, tool, result.Content ?? throw new ToolContractException("Tool result has no model content."),
                result.Metadata is null ? null : JsonSerializer.SerializeToNode(result.Metadata)!.AsObject(), completion.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is PermissionBlockedException or PermissionDeclinedException or QuestionCancelledException)
        {
            using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
            await FailToolAsync(id, tool, error is PermissionDeclinedException or QuestionCancelledException
                ? new SessionStructuredError("aborted", error is QuestionCancelledException ? error.Message : "The user declined this tool call")
                : SessionFailure.From(error), settlement.Token).ConfigureAwait(false);
            return error; // Preserve control flow: permission rejection must not start another model step.
        }
        catch (Exception error) { return error; } // Propagated as control/failure after all owned calls settle.
    }

    private async Task SuccessAsync(string id, Tool tool, IReadOnlyList<ToolContent> content, JsonObject? metadata,
        CancellationToken ct, JsonObject? resultState = null)
    {
        if (content.Count == 0) throw new ToolContractException("Tool result has no model content.");
        var data = new JsonObject
        {
            ["id"] = id, ["executed"] = tool.Hosted,
            ["content"] = new JsonArray(content.Select(item => JsonSerializer.SerializeToNode(item, OpenCodeJsonContext.Default.ToolContent)).ToArray())
        };
        if (metadata is not null) data["metadata"] = metadata;
        if (resultState is not null) data["resultState"] = resultState;
        await AppendAsync("session.tool.success", data, ct).ConfigureAwait(false);
        tool.Settled = true;
    }

    private static IReadOnlyList<ToolContent> HostedContent(LlmToolResult result) => result switch
    {
        LlmToolResult.Text text => [new ToolTextContent(text.Value)],
        LlmToolResult.Json json => [new ToolTextContent(ValueText(json.Value))],
        LlmToolResult.Content content when !content.Value.IsDefault && content.Value.IsEmpty => [new ToolTextContent("[]")],
        LlmToolResult.Content content when !content.Value.IsDefaultOrEmpty => content.Value.Select(part => part switch
        {
            LlmContent.Text text => (ToolContent)new ToolTextContent(text.Value),
            LlmContent.Media media => new ToolFileContent($"data:{media.MediaType};base64,{media.Base64}", media.MediaType, media.Filename),
            _ => throw new NotSupportedException("Hosted tool content has no canonical text/file representation.")
        }).ToArray(),
        _ => throw new NotSupportedException("Unsupported hosted tool result.")
    };

    private static string ValueText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static bool IsInterruptedStream(LlmException error) => error.Reason is
        LlmFailure.InvalidProviderOutput { IncompleteStream: true } or
        LlmFailure.Transport { Operation: LlmTransportOperation.Read };

    private Tool GetTool(string id, string name) => _tools.TryGetValue(id, out var tool) && tool.Name == name
        ? tool : throw new JsonException("Tool identity changed or tool input has not started.");

    private static Fragment Require(Fragment? fragment, string id) => fragment?.Id == id
        ? fragment : throw new JsonException("Fragment identity changed or content has not started.");

    private static void Merge(Fragment fragment, JsonObject? state)
    {
        if (state is null) return;
        fragment.State ??= new JsonObject();
        foreach (var pair in state) fragment.State[pair.Key] = pair.Value?.DeepClone();
    }

    private JsonObject? State(LlmEvent item)
    {
        if (item.ProviderMetadata.Count == 0) return null;
        if (!item.ProviderMetadata.TryGetValue(model.ProviderMetadataKey, out var state))
            throw new NotSupportedException("Provider metadata does not match the resolved transport namespace.");
        return JsonNode.Parse(state.GetRawText()) as JsonObject
            ?? throw new JsonException("Provider state must be an object.");
    }

    private Task<OpenCodeEvent> AppendAsync(string type, JsonObject data, CancellationToken ct)
    {
        data["sessionID"] = sessionId.Value;
        data["assistantMessageID"] = _assistantId.Value;
        return store.AppendAssistantEventAsync(type, JsonSerializer.SerializeToElement(data), ct);
    }

    private static string Finish(OpenCode.Core.Llm.LlmFinishReason reason) => reason switch
    {
        OpenCode.Core.Llm.LlmFinishReason.Stop => "stop",
        OpenCode.Core.Llm.LlmFinishReason.Length => "length",
        OpenCode.Core.Llm.LlmFinishReason.ContentFilter => "content-filter",
        OpenCode.Core.Llm.LlmFinishReason.ToolCalls => "tool-calls",
        OpenCode.Core.Llm.LlmFinishReason.Error => "error",
        OpenCode.Core.Llm.LlmFinishReason.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
