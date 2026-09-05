namespace OpenCode.Core.Session;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Core.CodeMode;
using OpenCode.Core.Config;
using OpenCode.Core.Instructions;
using OpenCode.Core.Llm;
using OpenCode.Core.Locations;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Schema;

public sealed partial class SessionExecutionEngine
{
    /// <summary>
    /// One transient generation from settled Session context. Does not admit input, claim/wake execution,
    /// publish usage/messages, execute tools, or commit observed instruction changes.
    /// </summary>
    public async Task<string> GenerateAsync(SessionId sessionId, string prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();
        var session = await sessionStore.GetSessionAsync(sessionId, ct) ?? throw new SessionMutationNotFoundException(sessionId);
        if (session.Location.WorkspaceId is not null) throw new NotSupportedException("Session generation requires local Location routing.");
        var placement = PermissionLocationMap.Canonical(session.Location);
        await using var lease = await AcquireToolsAsync(session.Location, ct);
        session = await sessionStore.GetSessionAsync(sessionId, ct) ?? throw new SessionMutationNotFoundException(sessionId);
        if (PermissionLocationMap.Canonical(session.Location) != placement)
            throw new OperationCanceledException("Session placement changed during generation selection.", ct);
        var document = ConfigLoader.LoadDocument(directory: session.Location.Directory);
        var agent = await ResolveAgentAsync(session, ct);
        var mcp = lease is null ? null : await lease.Mcp.ObserveAsync(McpInstructionSource.Configuration(session.Location.Directory, document), ct);
        var snapshot = lease is null ? null : (await lease.SnapshotAsync(session.Id, agent.Id, ct)).WithCodeMode(new JintCodeModeEvaluator(sessionStore.Clock), _codeModeLimits, sessionStore.Clock);
        RequireSnapshot(snapshot);
        var instructions = await sessionStore.ObserveInstructionsAsync(session, agent.Id.Value, document, ct, agent,
            snapshot?.Definitions.Select(definition => definition.Name).ToArray(), lease?.Location.Project.Directory,
            mcp: mcp is null ? null : McpInstructionSource.FromObservation(mcp, agent),
            codeMode: CodeModeInstructionSource.Create(snapshot?.CodeModeDiscovery));
        var model = await providerResolver.ResolveAsync(ct: ct, directory: session.Location.Directory, sessionModel: session.Model, sessionId: session.Id.Value);
        RequireToolContract(document.Deserialize<OpenCodeConfig>() ?? new(), model.Selection, agent);
        var history = await sessionStore.PreviewExecutionContextAsync(sessionId, instructions, ct);
        var metadata = (await providerResolver.ReadCatalogAsync(session.Location.Directory, ct)).Models.FirstOrDefault(item =>
            item.ProviderId == model.Selection.ProviderId && item.Id == model.Selection.Id)
            ?? throw new CatalogMetadataUnavailableException("Selected model metadata is unavailable for Session generation.");
        var messages = SessionHistory.Lower(history.Messages, model.Selection, model.ProviderMetadataKey);
        if (history.Update.Length > 0) messages = messages.Add(new LlmMessage(LlmRole.System, [new LlmContent.Text(history.Update)]));
        messages = messages.Add(new LlmMessage(LlmRole.User, [new LlmContent.Text(prompt)]));
        var lineage = (session.Fork?.SessionId ?? session.Id).Value;
        var request = new LlmRequest(model.ModelId, SessionHistory.PrepareMedia(messages, metadata.Capabilities?.Input))
        {
            System = new[] { instructions.System, history.Initial }.Where(text => text.Length > 0).Select(text => new LlmSystemPart(text)).ToImmutableArray(),
            Tools = snapshot is null ? [] : await SubagentTool.PrepareDefinitionsAsync(snapshot.Definitions, session.Location.Directory, agent, ct),
            // Source generation advertises the captured tools but never dispatches a returned call.
            ToolChoice = snapshot is null ? new LlmToolChoice.None() : null,
            PromptCacheKey = Regex.IsMatch(lineage, "^ses_[0-9a-f]{64}$") ? lineage[4..] : lineage,
            Http = new LlmHttpOptions
            {
                Headers = SessionRequestIdentity.Headers(session, agent.Request.Headers, identity),
                Body = agent.Request.Body.ToImmutableDictionary(StringComparer.Ordinal)
            }
        };
        Trace.TraceInformation("Sending Session generation request for {0} with {1}/{2}", sessionId.Value, model.Selection.ProviderId, model.Selection.Id);
        var fragments = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var order = new List<string>();
        var terminal = false;
        LlmUsage? usage = null;
        // Source LLM.generate folds one structured stream. No SessionAttempt, retries, or local tool execution.
        await foreach (var item in model.Client.StreamAsync(request, ct))
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case LlmEvent.TextDelta delta:
                    if (!fragments.TryGetValue(delta.Id, out var text))
                    {
                        fragments.Add(delta.Id, text = new StringBuilder());
                        order.Add(delta.Id);
                    }
                    text.Append(delta.Text);
                    break;
                case LlmEvent.TextEnd { Text: not null } end:
                    if (!fragments.ContainsKey(end.Id)) order.Add(end.Id);
                    fragments[end.Id] = new StringBuilder(end.Text);
                    break;
                case LlmEvent.StepFinish step when step.Usage is not null: usage = step.Usage; break;
                case LlmEvent.Finish finish:
                    terminal = true;
                    usage = finish.Usage ?? usage;
                    break;
                case LlmEvent.ProviderError:
                    // LLMResponse.complete treats an emitted provider-error as terminal; response.text
                    // still projects only actual text. Thrown provider/transport errors propagate normally.
                    terminal = true;
                    break;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!terminal) throw new LlmException(new LlmFailure.InvalidProviderOutput("Session generation stream ended without a terminal response.", true));
        Trace.TraceInformation("Session generation usage diagnostic: input={0}, output={1}", usage?.InputTokens, usage?.OutputTokens);
        return string.Concat(order.Select(id => fragments[id].ToString()));
    }
}
