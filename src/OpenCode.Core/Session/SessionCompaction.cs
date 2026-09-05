namespace OpenCode.Core.Session;

using System.Text;
using System.Text.Json;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Llm;
using OpenCode.Schema;

internal sealed record CompactionOutcome(bool Completed, SessionStructuredError? Error = null);

internal sealed class SessionCompaction(SessionStore store, ProviderResolver providers, SessionRequestIdentity? identity = null)
{
    internal async Task<CatalogModelInfo> MetadataAsync(SessionInfo session, ResolvedModel resolved, CancellationToken ct) =>
        (await providers.ReadCatalogAsync(session.Location.Directory, ct).ConfigureAwait(false)).Models.FirstOrDefault(model =>
            model.ProviderId == resolved.Selection.ProviderId && model.Id == resolved.Selection.Id)
        ?? throw new CatalogMetadataUnavailableException("Selected model metadata is unavailable for compaction.");

    internal async Task<CompactionOutcome> RunAsync(SessionInfo session, IReadOnlyList<JsonElement> messages,
        CompactionSettings settings, string reason, MessageId? inputId, bool started, ResolvedModel? resolved, CancellationToken ct,
        CatalogModelInfo? metadata = null)
    {
        var plan = CompactionPlan.Create(messages, settings.KeepTokens);
        if (plan is null) return await FailAsync(session.Id, reason, new SessionStructuredError("compaction.unavailable", "Nothing to compact yet"), inputId, ct).ConfigureAwait(false);
        try
        {
            // Manual resolution is deliberately after content planning.
            resolved ??= await providers.ResolveAsync(ct: ct, directory: session.Location.Directory, sessionModel: session.Model, sessionId: session.Id.Value).ConfigureAwait(false);
            metadata ??= await MetadataAsync(session, resolved, ct).ConfigureAwait(false);
            SessionExecutionEngine.RequireToolContract(ConfigLoader.LoadConfig(directory: session.Location.Directory), resolved.Selection);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return await FailAsync(session.Id, reason, SessionFailure.From(error), inputId, ct).ConfigureAwait(false);
        }
        if (!started)
            await store.PublishCompactionAsync(session.Id, CompactionProjector.Started, new SessionCompactionStartedEventData(session.Id, reason, plan.Recent, inputId), ct).ConfigureAwait(false);

        var chunks = new StringBuilder();
        SessionStructuredError? failure = null;
        TokenUsageInfo? tokens = null;
        double cost = 0;
        Exception? interruption = null;
        try
        {
            var request = new LlmRequest(resolved.ModelId, [new LlmMessage(LlmRole.User, [new LlmContent.Text(plan.Prompt)])])
            {
                System = [], Tools = [], ToolChoice = new LlmToolChoice.None(),
                Http = new LlmHttpOptions
                {
                    Headers = SessionRequestIdentity.Headers(session, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, identity)
                }
            };
            // Auxiliary summary generation is one physical call, not a normal assistant step.
            await foreach (var item in resolved.Client.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (item)
                {
                    case LlmEvent.TextDelta text:
                        chunks.Append(text.Text);
                        SessionEvents.CompactionDelta(session.Id, text.Text, store.Clock);
                        break;
                    case LlmEvent.StepFinish finish:
                        var usage = SessionUsage.Tokens(finish.Usage);
                        tokens = tokens is null ? usage : SessionUsage.Add(tokens, usage);
                        cost += SessionUsage.Cost(metadata.Cost, usage).Amount;
                        break;
                    case LlmEvent.ProviderError error:
                        failure = new SessionStructuredError(
                            error.Reason is LlmFailure.InvalidRequest { Classification: LlmFailureClassification.ContextOverflow }
                                ? "provider.invalid-request" : "provider.error", error.Reason.Message);
                        break;
                    case LlmEvent.ToolCall or LlmEvent.ToolResult or LlmEvent.ToolError or LlmEvent.ToolInputStart or LlmEvent.ToolInputDelta or LlmEvent.ToolInputEnd or LlmEvent.ToolInputError:
                        throw new NotSupportedException("Compaction summaries do not execute or accept tool calls.");
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) { interruption = error; }
        catch (Exception error) { failure = SessionFailure.From(error); }

        using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock);
        if (tokens is not null)
            await store.PublishCompactionAsync(session.Id, CompactionProjector.Usage,
                new SessionUsageRecordedEventData(session.Id, "compaction", Money.FromExisting(cost), tokens), settlement.Token).ConfigureAwait(false);
        if (interruption is not null)
        {
            // The manual-control owner settles interruption, including failures before streaming.
            if (reason == "auto")
                await FailAsync(session.Id, reason, new SessionStructuredError("compaction.interrupted", "Compaction was interrupted"), inputId, settlement.Token).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(interruption).Throw();
        }
        var summary = chunks.ToString();
        if (failure is not null || string.IsNullOrWhiteSpace(summary))
            return await FailAsync(session.Id, reason, failure ?? new SessionStructuredError("compaction.failed", "Compaction produced no summary"), inputId, settlement.Token).ConfigureAwait(false);
        await store.PublishCompactionAsync(session.Id, CompactionProjector.Ended, new SessionCompactionEndedEventData(session.Id, reason, summary, plan.Recent), settlement.Token).ConfigureAwait(false);
        return new CompactionOutcome(true);
    }

    internal async Task<CompactionOutcome> FailAsync(SessionId id, string reason, SessionStructuredError error, MessageId? inputId, CancellationToken ct)
    {
        await store.PublishCompactionAsync(id, CompactionProjector.Failed, new SessionCompactionFailedEventData(id, reason, error, inputId), ct).ConfigureAwait(false);
        return new CompactionOutcome(false, error);
    }
}
