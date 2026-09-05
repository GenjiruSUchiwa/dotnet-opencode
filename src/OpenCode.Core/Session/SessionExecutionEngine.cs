namespace OpenCode.Core.Session;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Immutable;
using System.Threading.Channels;
using OpenCode.Core.Config;
using OpenCode.Core.CodeMode;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Instructions;
using OpenCode.Core.Llm;
using OpenCode.Core.Tools;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Core.Agent;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Core.Session.Transfer;
using OpenCode.Core.Snapshot;
using OpenCode.Schema;

/// <summary>Native execution readiness, not a provider capability or public protocol event.</summary>
public sealed record SessionExecutionCapabilities(bool CanExecute, bool Instructions, bool Tools, string? UnavailableReason);

/// <summary>
/// Session-ID execution over durable inbox/history with caller or host ownership.
/// Tool execution requires the host's paired authoritative Location composition.
/// </summary>
public sealed partial class SessionExecutionEngine(SessionStore sessionStore, ProviderResolver providerResolver, ToolRegistry tools,
    ToolLocationFactory? toolFactory = null, PermissionLocationMap? toolLocations = null, SessionMovement? movement = null,
    SessionPromptPreparation? prompts = null, CodeModeLimits? codeModeLimits = null, SessionSnapshotLocations? snapshots = null,
    SessionTitleService? titles = null, SessionRequestIdentity? identity = null)
{
    private readonly SessionPromptPreparation _prompts = prompts ?? new SessionPromptPreparation(sessionStore);
    // Explicit native host policy, not a claim that upstream's unlimited defaults are finite.
    private readonly CodeModeLimits _codeModeLimits = codeModeLimits ?? new CodeModeLimits(
        timeoutMilliseconds: 60_000, maxToolCalls: 64, maxOutputBytes: 1024 * 1024,
        maxAllocatedBytes: 64 * 1024 * 1024, maxRecursionDepth: 64,
        maxSourceBytes: 262_144, maxBoundaryBytes: 4_194_304, maxExecutionChecks: 100_000, maxSyntaxNodes: 20_000);
    // Retained for SDK callers. The execution path never advertises or invokes this registry.
    public ToolRegistry Tools => tools;

    /// <summary>Supported engine features; CheckReadinessAsync validates a specific session/configuration.</summary>
    public SessionExecutionCapabilities Capabilities => new(true, true, toolFactory is not null && toolLocations is not null, null);

    public bool IsActive(SessionId sessionId) => SessionRunCoordinator.IsActive(sessionId);

    /// <summary>Process-owned sessions, including cleanup. Protocol maps each ID to { type: "running" }.</summary>
    public IReadOnlySet<SessionId> ActiveSessionIds => SessionRunCoordinator.Snapshot();

    /// <summary>
    /// Observe the complete supported instruction composition without admitting
    /// input or committing a new epoch. Unsupported producers fail explicitly.
    /// </summary>
    public async Task CheckReadinessAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var session = await sessionStore.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Session not found.");
        if (!IsActive(sessionId)) await sessionStore.RequireExecutionReadyAsync(sessionId, ct).ConfigureAwait(false);
        var document = ConfigLoader.LoadDocument(directory: session.Location.Directory);
        var agent = await ResolveAgentAsync(session, ct).ConfigureAwait(false);
        var lease = await AcquireToolsAsync(session.Location, ct).ConfigureAwait(false);
        await using var leaseLifetime = new OptionalToolLease(lease).ConfigureAwait(false);
        var mcp = lease is null ? null : await lease.Mcp.ObserveAsync(
            McpInstructionSource.Configuration(session.Location.Directory, document), ct).ConfigureAwait(false);
        var snapshot = lease is null ? null : (await lease.SnapshotAsync(session.Id, agent.Id, ct).ConfigureAwait(false)).WithCodeMode(new JintCodeModeEvaluator(sessionStore.Clock), _codeModeLimits, sessionStore.Clock);
        RequireSnapshot(snapshot);
        _ = SessionToolOutput.FromConfig(document);
        _ = CompactionSettings.Read(session.Location.Directory, document);
        await sessionStore.SelectInstructionsAsync(session, agent.Id.Value, document, true, ct, agent,
            snapshot?.Definitions.Select(definition => definition.Name).ToArray(), lease?.Location.Project.Directory,
            mcp: mcp is null ? null : McpInstructionSource.FromObservation(mcp, agent),
            codeMode: CodeModeInstructionSource.Create(snapshot?.CodeModeDiscovery)).ConfigureAwait(false);
    }

    /// <summary>Mandatory ReadTool callback, bound to the same loaded permission Location as the executing tool.</summary>
    public Task LoadReadInstructionsAsync(SessionId sessionId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        return SessionRunCoordinator.AdmitAsync(sessionId, async () =>
        {
            var session = await sessionStore.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Session not found.");
            if (toolLocations is null) throw new NotSupportedException("Read instructions require the shared tool Location map.");
            var lease = await toolLocations.TryAcquireLoadedAsync(session.Location, ct).ConfigureAwait(false)
                ?? throw new NotSupportedException("Read instructions require the executing tool's loaded Location.");
            await using var leaseLifetime = lease.ConfigureAwait(false);
            await sessionStore.LoadReadInstructionsAsync(sessionId, paths, lease.Location.Project.Directory, ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    public Task<SessionInboxItem> AdmitAsync(SessionId sessionId, string text, MessageId? id = null,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default) =>
        AdmitPromptAsync(sessionId, new PromptInput(text), id, delivery: delivery, ct: ct);

    /// <summary>Reconcile, prepare, then durably admit. The host wakes only after this returns.</summary>
    public Task<SessionInboxItem> AdmitPromptAsync(SessionId sessionId, PromptInput input, MessageId? id = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null, InboxDeliveryMode delivery = InboxDeliveryMode.Steer,
        CancellationToken ct = default) => _prompts.AdmitAsync(sessionId, input, id, metadata, delivery, ct);

    /// <summary>Durably admit manual compaction, then request an advisory host-owned drain.</summary>
    public async Task<SessionInboxItem> RequestCompactionAsync(SessionId sessionId, CancellationToken lifetime,
        MessageId? id = null, InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Compaction execution requires a host-owned lifetime.", nameof(lifetime));
        var admitted = await sessionStore.AdmitCompactionAsync(sessionId, id, delivery, ct).ConfigureAwait(false);
        await WakeAsync(sessionId, lifetime).ConfigureAwait(false);
        return admitted;
    }

    public IAsyncEnumerable<string> PromptAsync(SessionId sessionId, string promptText,
        string? modelId = null, string? variant = null, CancellationToken ct = default,
        MessageId? messageId = null, InboxDeliveryMode delivery = InboxDeliveryMode.Steer, bool resume = true) =>
        PromptAsync(sessionId, new PromptInput(promptText), modelId, variant, ct, messageId, delivery, resume);

    public async IAsyncEnumerable<string> PromptAsync(SessionId sessionId, PromptInput input,
        string? modelId = null, string? variant = null, [EnumeratorCancellation] CancellationToken ct = default,
        MessageId? messageId = null, InboxDeliveryMode delivery = InboxDeliveryMode.Steer, bool resume = true,
        IReadOnlyDictionary<string, JsonElement>? metadata = null, CancellationToken shutdown = default)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown);
        var id = messageId ?? MessageId.Create();
        if (!resume)
        {
            var existing = await sessionStore.ReconcileInboxAsync(sessionId, id, "user", delivery, requestCancellation.Token).ConfigureAwait(false);
            if (existing is not null) yield break;
            if (modelId is not null || variant is not null)
                throw new NotSupportedException("Admission-only prompts cannot retain a transient model override; use session model selection.");
            await AdmitPromptAsync(sessionId, input, id, metadata, delivery, requestCancellation.Token).ConfigureAwait(false);
            yield break;
        }
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation.Token);
        var readerCancelled = false;
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var running = PumpAsync();
        try
        {
            await foreach (var block in channel.Reader.ReadAllAsync(requestCancellation.Token).ConfigureAwait(false)) yield return block;
            await running.ConfigureAwait(false);
        }
        finally
        {
            readerCancelled = !running.IsCompleted && !shutdown.IsCancellationRequested;
            // Observe the owner through settlement even when enumeration stops early.
            try { await owner.CancelAsync().ConfigureAwait(false); }
            finally { await running.ConfigureAwait(false); }
        }

        async Task PumpAsync()
        {
            Exception? failure = null;
            try
            {
                await RunAsync(sessionId, true, value => channel.Writer.TryWrite(value), modelId, variant, owner.Token,
                    async token =>
                    {
                        await AdmitPromptAsync(sessionId, input, id, metadata, delivery, token).ConfigureAwait(false);
                    }, reconcile: async token =>
                    {
                        return await sessionStore.ReconcileInboxAsync(sessionId, id, "user", delivery, token).ConfigureAwait(false) is not null;
                    }, cancellationReason: shutdown.CanBeCanceled ? "shutdown" : "user",
                    interruptionReason: reason => reason == "user" || ct.IsCancellationRequested || readerCancelled || !shutdown.IsCancellationRequested ? "user" : "shutdown").ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            finally { channel.Writer.TryComplete(failure); }
        }
    }

    /// <summary>Joins active work or resumes a pending/delivered input; does not admit a new message.</summary>
    public async Task ResumeAsync(SessionId sessionId, CancellationToken ct = default, CancellationToken shutdown = default)
    {
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown);
        await RunAsync(sessionId, false, _ => { }, null, null, owner.Token,
            cancellationReason: shutdown.CanBeCanceled ? "shutdown" : "user",
            interruptionReason: reason => reason == "user" || ct.IsCancellationRequested || !shutdown.IsCancellationRequested ? "user" : "shutdown").ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit run/join with a host-owned lifetime. Unlike advisory WakeAsync,
    /// waits for execution settlement and requests a forced drain when idle.
    /// Cancellation of the host owner records shutdown, preserving its claim.
    /// </summary>
    public Task ResumeHostedAsync(SessionId sessionId, CancellationToken lifetime)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Hosted resume requires a cancellable host lifetime.", nameof(lifetime));
        return RunAsync(sessionId, false, _ => { }, null, null, lifetime, cancellationReason: "shutdown");
    }

    internal Task ResumeChildJobAsync(SessionId sessionId, CancellationToken work, CancellationToken host) =>
        RunAsync(sessionId, false, _ => { }, null, null, work, cancellationReason: "shutdown",
            interruptionReason: reason => reason == "user" || !host.IsCancellationRequested ? "user" : "shutdown");

    /// <summary>
    /// Startup-only recovery for supported top-level claims. The host must first
    /// confirm the previous process is dead and own the managed registration lock.
    /// Does not clear child claims or fabricate background-job recovery.
    /// </summary>
    public Task<SessionRecoveryReport> RecoverSuspendedAsync(CancellationToken lifetime, int maxAttempts = 10) =>
        RecoverSuspendedAsync(lifetime, maxAttempts, null);

    internal async Task<SessionRecoveryReport> RecoverSuspendedAsync(CancellationToken lifetime, int maxAttempts, RestartScope? scope)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Recovery requires a host-owned cancellation lifetime.", nameof(lifetime));
        ArgumentOutOfRangeException.ThrowIfNegative(maxAttempts);
        var scheduled = new List<SessionId>();
        var exhausted = new List<SessionId>();
        var skipped = new List<SessionId>();
        var blocked = new Dictionary<SessionId, string>();
        foreach (var id in await sessionStore.ListSuspendedAsync(lifetime).ConfigureAwait(false))
        {
            lifetime.ThrowIfCancellationRequested();
            if (IsActive(id)) { skipped.Add(id); continue; }
            try
            {
                await SessionRunCoordinator.ScheduleAsync(id, registered => RunAsync(id, false, _ => { }, null, null, lifetime,
                    admission: async token =>
                    {
                        var preparation = await sessionStore.PrepareRestartAsync(id, maxAttempts, token, scope, localMoves: movement is not null).ConfigureAwait(false);
                        if (preparation != RestartPreparation.Ready) throw new RecoveryNotScheduledException(preparation);
                    }, cancellationReason: "shutdown", registered: registered, recovering: true), lifetime).ConfigureAwait(false);
                scheduled.Add(id);
            }
            catch (SessionAlreadyOwnedException) { skipped.Add(id); }
            catch (RecoveryNotScheduledException result)
            {
                if (result.Preparation == RestartPreparation.Exhausted) exhausted.Add(id);
                else skipped.Add(id);
            }
            catch (NotSupportedException error) { blocked[id] = error.Message; }
            catch (SessionMutationInProgressException error) { blocked[id] = error.Message; }
        }
        return new SessionRecoveryReport(scheduled.ToArray(), exhausted.ToArray(), skipped.ToArray(), blocked);
    }

    /// <summary>Startup-only child ownership. The returned task is the actual recovered drain, never a second resume/join.</summary>
    internal Task ResumeRecoveredChildAsync(SessionId id, CancellationToken lifetime, int maxAttempts, RestartScope scope, Action registered,
        CancellationToken host = default) =>
        RunAsync(id, false, _ => { }, null, null, lifetime, admission: async token =>
        {
            var preparation = await sessionStore.PrepareRestartAsync(id, maxAttempts, token, scope, child: true, localMoves: movement is not null).ConfigureAwait(false);
            if (preparation != RestartPreparation.Ready) throw new RecoveryNotScheduledException(preparation);
        }, cancellationReason: "shutdown", registered: registered, recovering: true,
            interruptionReason: reason => reason == "user" || host.CanBeCanceled && !host.IsCancellationRequested ? "user" : reason);

    /// <summary>
    /// Advisory non-forced wake. Acknowledges scheduling, not model completion.
    /// The supplied host lifetime owns the execution; cancellation is shutdown,
    /// whereas InterruptAsync explicitly records user interruption.
    /// </summary>
    public Task WakeAsync(SessionId sessionId, CancellationToken lifetime)
        => WakeAsync(sessionId, InboxPromotable.Input, lifetime);

    private Task WakeAsync(SessionId sessionId, InboxPromotable scope, CancellationToken lifetime)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = TrackDrain(settled.Task); // Tracked for host disposal; scheduling acknowledges registration, not completion.
        try
        {
            return SessionRunCoordinator.ScheduleAsync(sessionId, async registered =>
            {
                try { await RunAsync(sessionId, true, _ => { }, null, null, lifetime, cancellationReason: "shutdown", registered: registered, promotable: scope).ConfigureAwait(false); }
                finally { settled.TrySetResult(); }
            }, lifetime);
        }
        catch { settled.TrySetResult(); throw; }
    }

    public Task AwaitIdleAsync(SessionId sessionId, CancellationToken ct = default) => SessionRunCoordinator.AwaitIdleAsync(sessionId, ct);

    /// <summary>Run a domain mutation with admissions/drains excluded through interruption and settlement.</summary>
    public Task WithIdleMutationAsync(SessionId sessionId, Func<CancellationToken, Task> mutation, CancellationToken ct = default) =>
        SessionRunCoordinator.WithIdleMutationAsync(sessionId, mutation, ct);

    /// <summary>
    /// Reserve against admission and execution, interrupt and await settlement, then
    /// hold the returned lease through transactional removal. Does not delete data.
    /// </summary>
    public Task<IAsyncDisposable> ReserveRemovalAsync(SessionId sessionId, CancellationToken ct = default) =>
        SessionRunCoordinator.ReserveRemovalAsync(sessionId, ct);

    public async Task<bool> InterruptAsync(SessionId sessionId, CancellationToken ct = default)
    {
        if (await sessionStore.GetSessionAsync(sessionId, ct).ConfigureAwait(false) is null) throw new SessionMutationNotFoundException(sessionId);
        return SessionRunCoordinator.Interrupt(sessionId);
    }

    /// <summary>Interrupts locally owned work and optionally schedules only remaining steering/control work.</summary>
    public async Task<bool> InterruptAsync(SessionId sessionId, SessionInterruptOptions options,
        CancellationToken lifetime, CancellationToken ct = default)
    {
        if (options.Continue && !lifetime.CanBeCanceled)
            throw new ArgumentException("Interrupt continuation requires a host-owned lifetime.", nameof(lifetime));
        var interrupted = await InterruptAsync(sessionId, ct).ConfigureAwait(false);
        if (!options.Continue) return interrupted;
        var next = await sessionStore.NextPromotableInboxAsync(sessionId, InboxPromotable.Input, ct).ConfigureAwait(false);
        if (next is not null && (next.Delivery == InboxDeliveryMode.Steer || next.Payload is CompactionInboxPayload or MoveInboxPayload))
            await WakeAsync(sessionId, InboxPromotable.Steer, lifetime).ConfigureAwait(false);
        return interrupted;
    }

    private Task RunAsync(SessionId sessionId, bool wake, Action<string> output,
        string? modelId, string? variant, CancellationToken ct, Func<CancellationToken, Task>? admission = null,
        string cancellationReason = "user", Action? registered = null, Func<CancellationToken, Task<bool>>? reconcile = null,
        bool recovering = false, Func<string, string>? interruptionReason = null, InboxPromotable promotable = InboxPromotable.Input)
    {
        var force = !wake;
        var claimed = false;
        return TrackDrain(SessionRunCoordinator.RunAsync(sessionId, sessionStore.Clock, wake, output,
            async (_, token) =>
            {
                // Reject unsequenced history before lifecycle events could disguise it.
                await sessionStore.RequireExecutionReadyAsync(sessionId, token, recovering).ConfigureAwait(false);
                await sessionStore.AppendExecutionEventAsync(sessionId, "session.execution.started", token).ConfigureAwait(false);
                claimed = true;
            },
            async (emit, drainScope, token) =>
            {
                var continuing = false;
                var retrying = false;
                var entering = true;
                var settleTools = true;
                var assistantId = MessageId.Create();
                var retry = new SessionRetry(sessionStore.Clock);
                var overflowRecovery = true;
                var continuationRecovery = true;
                var compaction = new SessionCompaction(sessionStore, providerResolver, identity);
                double step = 1;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var scope = entering || !continuing ? InboxPromotable.Input : InboxPromotable.Steer;
                    var pending = retrying ? null : await sessionStore.NextPromotableInboxAsync(sessionId, scope, token).ConfigureAwait(false);
                    if (pending is null && !continuing && !force && !retrying) return;
                    if (!continuing && !force && !retrying && drainScope == InboxPromotable.Steer
                        && pending?.Delivery == InboxDeliveryMode.Queue && pending.Payload is not (CompactionInboxPayload or MoveInboxPayload)) return;
                    if (settleTools)
                    {
                        await SettleStaleToolsAsync(sessionId, token).ConfigureAwait(false);
                        settleTools = false;
                    }
                    if (!retrying && !continuing && pending?.Delivery != InboxDeliveryMode.Steer)
                    {
                        entering = true;
                        step = 1;
                    }
                    if (pending?.Payload is MoveInboxPayload)
                    {
                        if (movement is null) throw new NotSupportedException("Movement requires the host's shared SessionMovement service.");
                        var moved = await movement.TryDeliverAsync(sessionId, scope, CloseSourceHttpTransportAsync, token).ConfigureAwait(false);
                        if (moved is null) continue;
                        continuing = !entering && continuing;
                        if (!continuing) step = 1;
                        entering = true;
                        settleTools = true;
                        force = false;
                        // Re-read saved placement before resolving destination configuration, tools, and instructions.
                        continue;
                    }
                    if (pending?.Payload is CompactionInboxPayload)
                    {
                        var target = await sessionStore.GetSessionAsync(sessionId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Session not found.");
                        if (target.Location.WorkspaceId is not null) throw new NotSupportedException("Compaction requires native workspace placement support.");
                        var settings = CompactionSettings.Read(target.Location.Directory, ConfigLoader.LoadDocument(directory: target.Location.Directory));
                        var input = await sessionStore.StartCompactionAsync(sessionId, scope, token).ConfigureAwait(false);
                        if (input is null) continue;
                        try
                        {
                            await compaction.RunAsync(target, await sessionStore.LoadExecutionHistoryAsync(sessionId, token).ConfigureAwait(false), settings,
                                "manual", input, true, null, token).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15), sessionStore.Clock);
                            await compaction.FailAsync(sessionId, "manual", error is OperationCanceledException
                                ? new SessionStructuredError("aborted", "Compaction cancelled") : SessionFailure.From(error), input, cleanup.Token).ConfigureAwait(false);
                            throw;
                        }
                        force = false;
                        continue;
                    }
                    var session = await sessionStore.GetSessionAsync(sessionId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Session not found.");
                    if (session.Location.WorkspaceId is not null)
                        throw new NotSupportedException("Explicit workspace execution requires Location service routing.");
                    var directory = session.Location.Directory;
                    var document = ConfigLoader.LoadDocument(directory: directory);
                    var config = document.Deserialize<OpenCodeConfig>() ?? new();
                    var agent = await ResolveAgentAsync(session, token).ConfigureAwait(false);
                    var lease = await AcquireToolsAsync(session.Location, token).ConfigureAwait(false);
                    await using var leaseLifetime = new OptionalToolLease(lease).ConfigureAwait(false);
                    var mcp = lease is null ? null : await lease.Mcp.ObserveAsync(
                        McpInstructionSource.Configuration(directory, document), token).ConfigureAwait(false);
                    var snapshot = lease is null ? null : (await lease.SnapshotAsync(session.Id, agent.Id, token).ConfigureAwait(false)).WithCodeMode(new JintCodeModeEvaluator(sessionStore.Clock), _codeModeLimits, sessionStore.Clock);
                    RequireSnapshot(snapshot);
                    // Source select/prepare happens before promotion: an unavailable initial
                    // instruction baseline must leave admitted input pending.
                    var instructions = await sessionStore.SelectInstructionsAsync(session, agent.Id.Value, document, false, token, agent,
                        snapshot?.Definitions.Select(definition => definition.Name).ToArray(), lease?.Location.Project.Directory,
                        mcp: mcp is null ? null : McpInstructionSource.FromObservation(mcp, agent),
                        codeMode: CodeModeInstructionSource.Create(snapshot?.CodeModeDiscovery)).ConfigureAwait(false);
                    var promoted = retrying ? 0 : await sessionStore.PromoteInboxAsync(sessionId,
                        entering && !continuing ? drainScope : InboxPromotable.Steer, token).ConfigureAwait(false);
                    if (promoted == 0 && !force && !continuing && !retrying) return;
                    if (promoted > 0)
                    {
                        step = 1;
                        if (session.ParentId is null && SessionTitleService.IsUntitled(session)) titles?.Schedule(sessionId);
                    }
                    force = false;
                    // Source context.load resolves the selected model after promotion. A routing
                    // failure must not leave input invisibly queued after its boundary was admitted.
                    var model = await providerResolver.ResolveAsync(modelId, variant, token, directory, session.Model, sessionId: session.Id.Value).ConfigureAwait(false);
                    RequireToolContract(config, model.Selection, agent);
                    var context = await sessionStore.LoadExecutionContextAsync(sessionId, instructions, token).ConfigureAwait(false);
                    var settingsForStep = CompactionSettings.Read(directory, document);
                    var modelMetadata = await compaction.MetadataAsync(session, model, token).ConfigureAwait(false);
                    if (settingsForStep.Required(context.Messages, modelMetadata))
                    {
                        var compacted = await compaction.RunAsync(session, context.Messages, settingsForStep, "auto", null, false, model, token, modelMetadata).ConfigureAwait(false);
                        if (!compacted.Completed) throw new SessionStepFailedException(compacted.Error!);
                        assistantId = MessageId.Create();
                        retrying = true;
                        continue;
                    }
                    var messages = SessionHistory.PrepareMedia(SessionHistory.Lower(context.Messages, model.Selection, model.ProviderMetadataKey), modelMetadata.Capabilities?.Input);
                    if (messages.Length == 0) throw new NotSupportedException("No model-visible text input is available.");
                    var limitReached = agent.Steps is { } limit && step >= limit;
                    if (limitReached) messages = messages.Add(new LlmMessage(LlmRole.Assistant, [new LlmContent.Text(SessionStepLimit.Prompt)]));
                    var request = new LlmRequest(model.ModelId, messages)
                    {
                        PromptCacheKey = SessionRequestIdentity.PromptCacheKey(session),
                        System = new[] { instructions.System, context.Initial }.Where(text => text.Length > 0)
                            .Select(text => new LlmSystemPart(text)).ToImmutableArray(),
                        Tools = snapshot is null ? [] : await SubagentTool.PrepareDefinitionsAsync(snapshot.Definitions, directory, agent, token).ConfigureAwait(false),
                        ToolChoice = snapshot is null || limitReached ? new LlmToolChoice.None() : null,
                        Http = new LlmHttpOptions
                        {
                            Headers = SessionRequestIdentity.Headers(session, agent.Request.Headers, identity),
                            Body = agent.Request.Body.ToImmutableDictionary(StringComparer.Ordinal)
                        }
                    };
                    var outcome = await new SessionAttempt(sessionStore, sessionId, model, agent.Id.Value, emit, snapshot,
                        SessionToolOutput.FromConfig(document), assistantId, retry, modelMetadata.Cost,
                         recoverOverflow: settingsForStep.Auto && overflowRecovery ? async cancellation =>
                            (await compaction.RunAsync(session, context.Messages, settingsForStep, "auto", null, false, model, cancellation, modelMetadata).ConfigureAwait(false)).Completed : null,
                        snapshots: snapshots is null ? null : await snapshots.TryGetAsync(session.Location, token).ConfigureAwait(false),
                        recoverContinuation: continuationRecovery)
                        .RunAsync(request, token).ConfigureAwait(false);
                    if (outcome is SessionAttemptOutcome.RecoverFull)
                    {
                        continuationRecovery = false;
                        retrying = true;
                        continue;
                    }
                    if (outcome is SessionAttemptOutcome.Compacted)
                    {
                        overflowRecovery = false;
                        assistantId = MessageId.Create();
                        retrying = true;
                        continue;
                    }
                    if (outcome is SessionAttemptOutcome.Retry scheduled)
                    {
                        retrying = true;
                        await SessionRetry.WaitAsync(sessionStore, sessionId, assistantId, scheduled, token).ConfigureAwait(false);
                        continue;
                    }
                    if (outcome is SessionAttemptOutcome.Continue interrupted)
                    {
                        await SessionRetry.WaitAsync(sessionStore, sessionId, assistantId,
                            new SessionAttemptOutcome.Retry(interrupted.Error, interrupted.Decision), token).ConfigureAwait(false);
                        // This existing generic transaction publisher also supports synthetic facts;
                        // the supplied definition owns the event type and projection, not the method name.
                        await sessionStore.PublishCompactionAsync(sessionId, SessionSyntheticProjector.Definition,
                            new SessionSyntheticData(sessionId,
                                "The previous response was interrupted. Continue from where you left off without repeating completed content."), token).ConfigureAwait(false);
                        assistantId = MessageId.Create();
                        retrying = true;
                        continue;
                    }
                    continuing = ((SessionAttemptOutcome.Completed)outcome).NeedsContinuation;
                    retrying = false;
                    entering = false;
                    assistantId = MessageId.Create();
                    retry = new SessionRetry(sessionStore.Clock);
                    overflowRecovery = true;
                    continuationRecovery = true;
                    step++;
                    // The next logical step reloads durable tool results and instructions;
                    // queued input stays parked during a tool continuation.
                }
            },
            async (failure, reason, token) =>
            {
                if (!claimed) return;
                if (failure is PermissionDeclinedException or QuestionCancelledException)
                    await sessionStore.AppendExecutionEventAsync(sessionId, "session.execution.interrupted", token, reason: "user").ConfigureAwait(false);
                else if (failure is OperationCanceledException)
                    await sessionStore.AppendExecutionEventAsync(sessionId, "session.execution.interrupted", token, reason: interruptionReason?.Invoke(reason) ?? reason).ConfigureAwait(false);
                else if (failure is not null)
                    await sessionStore.AppendExecutionEventAsync(sessionId, "session.execution.failed", token,
                        error: SessionFailure.From(failure)).ConfigureAwait(false);
                else
                    await sessionStore.AppendExecutionEventAsync(sessionId, "session.execution.succeeded", token).ConfigureAwait(false);
            }, ct, requireIdle: modelId is not null || variant is not null, admission: admission,
            cancellationReason: cancellationReason, registered: registered, reconcile: reconcile is null ? null : async token =>
            {
                var existing = await reconcile(token).ConfigureAwait(false);
                if (existing) { modelId = null; variant = null; }
                return existing;
            }, onlyIfIdle: recovering, scope: promotable));
    }

    private async Task SettleStaleToolsAsync(SessionId sessionId, CancellationToken ct)
    {
        foreach (var message in await sessionStore.LoadExecutionHistoryAsync(sessionId, ct).ConfigureAwait(false))
        {
            if (message.GetProperty("type").GetString() != "assistant") continue;
            foreach (var tool in message.GetProperty("content").EnumerateArray())
            {
                if (tool.GetProperty("type").GetString() != "tool") continue;
                var state = tool.GetProperty("state");
                if (state.GetProperty("status").GetString() is not ("streaming" or "running")) continue;
                var child = tool.GetProperty("name").GetString() == "subagent" && state.GetProperty("status").GetString() == "running" &&
                    state.TryGetProperty("metadata", out var progress) && progress.TryGetProperty("sessionID", out var target) && target.ValueKind == JsonValueKind.String
                    ? target.GetString() : null;
                var data = new System.Text.Json.Nodes.JsonObject
                {
                    ["sessionID"] = sessionId.Value,
                    ["assistantMessageID"] = message.GetProperty("id").GetString(),
                    ["id"] = tool.GetProperty("id").GetString(),
                    ["executed"] = tool.TryGetProperty("executed", out var executed) && executed.GetBoolean(),
                    ["error"] = JsonSerializer.SerializeToNode(new SessionStructuredError("aborted",
                        "Tool execution interrupted: " + tool.GetProperty("name").GetString() + (child is null ? "" : $" (sessionID: {child})")), OpenCodeJsonContext.Default.SessionStructuredError)
                };
                if (state.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && metadata.EnumerateObject().Any())
                    data["metadata"] = System.Text.Json.Nodes.JsonNode.Parse(metadata.GetRawText());
                await sessionStore.AppendAssistantEventAsync("session.tool.failed", JsonSerializer.SerializeToElement(data), ct).ConfigureAwait(false);
            }
        }
    }

    private static Task CloseSourceHttpTransportAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Current native adapters scope/dispose HTTP responses inside StreamAsync. This boundary
        // runs only after SessionAttempt and its tool tasks finish; no Session channel survives it.
        // A future persistent transport must replace this with its Session-scoped close operation.
        return Task.CompletedTask;
    }

    internal static void RequireToolContract(OpenCodeConfig config, ModelRef selection, AgentInfo? agent = null)
    {
        var provider = config.Providers?.GetValueOrDefault(selection.ProviderId);
        var model = provider?.Models?.GetValueOrDefault(selection.Id);
        var variant = model?.Variants?.FirstOrDefault(item => item.Id == selection.Variant);
        foreach (var body in new[] { provider?.Body, model?.Body, variant?.Body, agent?.Request.Body })
            if (body is not null && new[] { "tools", "tool_choice", "toolConfig", "functions", "function_call" }.Any(body.ContainsKey))
                throw new NotSupportedException("Body overrides cannot replace the captured request tool definitions or tool choice.");
        if (agent?.Request.Settings.Count > 0) throw new NotSupportedException("Agent request settings require their model-request adapter.");
    }

    private static async Task<AgentInfo> ResolveAgentAsync(SessionInfo session, CancellationToken ct) =>
        await AgentCatalog.ResolveAsync(session.Location.Directory, session.Agent is null ? null : AgentId.FromExisting(session.Agent), ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The selected agent is unavailable.");

    private async ValueTask<ToolLocationLease?> AcquireToolsAsync(LocationRef location, CancellationToken ct)
    {
        if (toolFactory is null && toolLocations is null) return null;
        if (toolFactory is null || toolLocations is null) throw new NotSupportedException("Tool execution requires a paired factory and the Server's shared permission Location map.");
        return await toolFactory.AcquireAsync(toolLocations, location, ct).ConfigureAwait(false);
    }

    private static void RequireSnapshot(ToolSnapshot? snapshot)
    {
        if (snapshot?.CodeModeCatalog is { Count: > 0 } && !snapshot.CodeModeExecutable)
            throw new NotSupportedException("The captured Code Mode catalog has no bound evaluator.");
    }

    // Tool-free hosts have no lease. Keep nullable cleanup awaitable without capturing the caller's context.
    private readonly struct OptionalToolLease(ToolLocationLease? lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

}
