namespace OpenCode.Sdk;

using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.CodeMode;
using OpenCode.Core.Llm;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Archive;
using OpenCode.Core.Session.Skills;
using OpenCode.Core.Session.Transfer;
using OpenCode.Core.Session.Subagents;
using OpenCode.Core.Snapshot;
using OpenCode.Core.Tools;
using OpenCode.Core.Forms;
using OpenCode.Core.Jobs;
using OpenCode.Core.Event.Log;
using OpenCode.Core.Shell.Jobs;
using OpenCode.Server.Services;
using OpenCode.Schema;

public sealed class OpenCodeClient : IAsyncDisposable
{
    private readonly OwnedSdkHost? _owned;
    private readonly CredentialStore _credentialStore;
    private readonly SessionStore _sessionStore;
    private readonly ProviderResolver _providerResolver;
    private readonly SessionExecutionEngine _engine;
    private readonly SessionInstructionEntries? _instructions;
    private readonly SessionQueries? _queries;
    private readonly SessionRevertOperations? _reverts;
    private readonly SessionSubagents? _subagents;
    private readonly SessionTitleService? _titles;

    public SessionStore Sessions => _sessionStore;
    public SessionTitleService Titles => _titles ?? throw new InvalidOperationException("The embedding host must supply its lifetime-owned SessionTitleService.");
    public Task GenerateTitleAsync(SessionId sessionId, CancellationToken ct = default) => RunAsync(token => Titles.GenerateAsync(sessionId, token), ct);
    public Task<string> GenerateAsync(SessionId sessionId, string prompt, CancellationToken ct = default) => RunAsync(token => _engine.GenerateAsync(sessionId, prompt, token), ct);
    public SessionSubagents Subagents => _subagents ?? throw new InvalidOperationException("The embedding host must supply its lifetime-owned SessionSubagents service.");
    public SessionRevertOperations Revert => _reverts ?? throw new InvalidOperationException("The embedding host must supply its SessionRevertOperations service.");
    public SessionQueries Queries => _queries ?? throw new InvalidOperationException("The embedding host must supply its SessionQueries service.");
    public Task<SessionMessage?> MessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct = default) => RunAsync(token => Queries.MessageAsync(sessionId, messageId, token), ct);
    public Task<IReadOnlyList<SessionMessage>> ContextAsync(SessionId sessionId, CancellationToken ct = default) => RunAsync(token => Queries.ContextAsync(sessionId, token), ct);
    public SessionInstructionEntries Instructions => _instructions ??
        throw new InvalidOperationException("The embedding host must supply its SessionInstructionEntries service.");
    public CredentialStore Credentials => _credentialStore;
    public ProviderResolver Providers => _providerResolver;
    public SessionExecutionCapabilities ExecutionCapabilities => _engine.Capabilities;

    public Task CheckReadinessAsync(SessionId sessionId, CancellationToken ct = default) => RunAsync(token => _engine.CheckReadinessAsync(sessionId, token), ct);

    public Task<SessionInboxItem> AdmitAsync(SessionId sessionId, string text, MessageId? id = null,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default) =>
        RunAsync(token => _engine.AdmitAsync(sessionId, text, id, delivery, token), ct);

    public Task<SessionInboxItem> AdmitPromptAsync(SessionId sessionId, PromptInput input, MessageId? id = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null, InboxDeliveryMode delivery = InboxDeliveryMode.Steer,
        CancellationToken ct = default) => RunAsync(token => _engine.AdmitPromptAsync(sessionId, input, id, metadata, delivery, token), ct);

    public Task ResumeAsync(SessionId sessionId, CancellationToken ct = default) =>
        RunAsync(_ => _engine.ResumeAsync(sessionId, ct, _owned?.Stopping ?? default), ct);

    /// <summary>Advisory execution after admission. The embedding host owns the required cancellable lifetime.</summary>
    public Task WakeAsync(SessionId sessionId, CancellationToken lifetime) => _owned is null ? _engine.WakeAsync(sessionId, lifetime) : _owned.WakeAsync(sessionId, lifetime);

    public Task WakeAsync(SessionId sessionId) => Owned.WakeAsync(sessionId, Owned.Stopping);

    public Task AwaitIdleAsync(SessionId sessionId, CancellationToken ct = default) => _engine.AwaitIdleAsync(sessionId, ct);

    public Task ResumeHostedAsync(SessionId sessionId, CancellationToken lifetime) => RunAsync(token => _engine.ResumeHostedAsync(sessionId, token), lifetime);

    /// <summary>Bind LocalToolOptions.LoadReadInstructions to this callback, not to an empty delegate.</summary>
    public Task LoadReadInstructionsAsync(SessionId sessionId, IReadOnlyList<string> paths, CancellationToken ct) =>
        RunAsync(token => _engine.LoadReadInstructionsAsync(sessionId, paths, token), ct);

    public Task<bool> InterruptAsync(SessionId sessionId, CancellationToken ct = default) => RunAsync(token => _engine.InterruptAsync(sessionId, token), ct);

    public bool IsActive(SessionId sessionId) => _engine.IsActive(sessionId);

    public OpenCodeClient(string? dbPath = null) : this(new OwnedSdkHost(new SdkHostOptions { DatabasePath = dbPath })) { }

    private OpenCodeClient(OwnedSdkHost host)
    {
        _owned = host;
        _credentialStore = _owned.Get<CredentialStore>();
        _sessionStore = _owned.Get<SessionStore>();
        _queries = _owned.Get<SessionQueries>();
        _instructions = _owned.Get<SessionInstructionEntries>();
        _providerResolver = _owned.Get<ProviderResolver>();
        _titles = _owned.Get<SessionTitleService>();
        _engine = _owned.Get<SessionExecutionEngine>();
        _reverts = _owned.Get<SessionRevertOperations>();
        _subagents = _owned.Get<SessionSubagents>();
    }

    /// <summary>
    /// Embeds the real local tool runner using the host's shared stores, factory and permission map.
    /// The host owns these services, database bootstrap, permission notifications/replies, and shutdown.
    /// DisposeAsync does not dispose injected services. Drain execution before closing their map or stores.
    /// </summary>
    public OpenCodeClient(SessionStore sessionStore, CredentialStore credentialStore, ProviderResolver providerResolver,
        ToolRegistry tools, ToolLocationFactory toolFactory, PermissionLocationMap toolLocations, SessionMovement? movement = null,
        SessionInstructionEntries? instructions = null, SessionPromptPreparation? prompts = null, SessionQueries? queries = null,
        CodeModeLimits? codeModeLimits = null, SessionSnapshotLocations? snapshots = null, SessionRevertOperations? reverts = null,
        SessionSubagents? subagents = null, SessionTitleService? titles = null, SessionRequestIdentity? identity = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _instructions = instructions;
        _queries = queries;
        _reverts = reverts;
        _subagents = subagents;
        _titles = titles;
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolFactory);
        ArgumentNullException.ThrowIfNull(toolLocations);
        _engine = new SessionExecutionEngine(sessionStore, providerResolver, tools, toolFactory, toolLocations, movement, prompts, codeModeLimits, snapshots, titles, identity);
    }

    public static Task<OpenCodeClient> CreateAsync(string? dbPath = null) => CreateAsync(new SdkHostOptions { DatabasePath = dbPath }, default);

    public static async Task<OpenCodeClient> CreateAsync(SdkHostOptions options, CancellationToken ct)
    {
        var client = new OpenCodeClient(new OwnedSdkHost(options));
        try { await client.Owned.InitializeAsync(ct); return client; }
        catch { await client.DisposeAsync(); throw; }
    }

    /// <summary>
    /// Streams a prompt to a specific session and records user and assistant messages in opencode.db.
    /// </summary>
    public IAsyncEnumerable<string> PromptAsync(
        SessionId sessionId,
        string promptText,
        string? modelId = null,
        string? variant = null,
        CancellationToken ct = default,
        MessageId? messageId = null,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer,
        bool resume = true)
    {
        return PromptAsync(sessionId, new PromptInput(promptText), modelId, variant, ct, messageId, delivery, resume);
    }

    public async IAsyncEnumerable<string> PromptAsync(SessionId sessionId, PromptInput input, string? modelId = null,
        string? variant = null, [EnumeratorCancellation] CancellationToken ct = default, MessageId? messageId = null,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, bool resume = true,
        IReadOnlyDictionary<string, JsonElement>? metadata = null)
    {
        _owned?.RequireOpen();
        await foreach (var text in _engine.PromptAsync(sessionId, input, modelId, variant, ct, messageId, delivery, resume, metadata, _owned?.Stopping ?? default))
            yield return text;
    }

    /// <summary>
    /// Ask a single question. Auto-creates a session in the current directory and streams the response.
    /// </summary>
    public async IAsyncEnumerable<string> AskAsync(
        string promptText,
        string? modelId = null,
        string? variant = null,
        string? directory = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!ExecutionCapabilities.CanExecute) throw new NotSupportedException(ExecutionCapabilities.UnavailableReason);
        var dir = directory ?? Directory.GetCurrentDirectory();
        var session = await RunAsync(token => _sessionStore.CreateSessionAsync(dir, ct: token), ct);

        await foreach (var chunk in PromptAsync(session.Id, promptText, modelId, variant, ct))
        {
            yield return chunk;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned is not null) await _owned.DisposeAsync();
    }

    public JobRuntime Jobs => Owned.Get<JobRuntime>();
    public Task BackgroundAsync(SessionId sessionId, CancellationToken ct = default) =>
        RunAsync(token => Owned.Get<SessionBackgroundService>().BackgroundAsync(sessionId, token), ct);
    public SessionEnvironment Environment => Owned.Get<SessionEnvironment>();
    public FormLocationServices FormLocations => Owned.Get<FormLocationServices>();
    public IAsyncEnumerable<OpenCodeEvent> EventsAsync(CancellationToken ct = default) => Owned.EventsAsync(ct);

    public async IAsyncEnumerable<DurableLogItem> LogAsync(SessionId sessionId, double? after = null, bool follow = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _owned?.RequireOpen();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _owned?.Stopping ?? default);
        await foreach (var item in _sessionStore.LogAsync(sessionId, after, follow, lifetime.Token)) yield return item;
    }

    public Task<SessionTransferData> ExportSessionAsync(SessionId sessionId, bool sanitize = false, CancellationToken ct = default) =>
        RunAsync(token => Owned.Get<SessionArchiveService>().ExportAsync(sessionId, sanitize, token), ct);

    public Task<SessionInfo> ImportSessionAsync(SessionTransferData data, LocationRef destination, CancellationToken ct = default) =>
        RunAsync(token => Owned.Get<SessionArchiveService>().ImportAsync(data, destination, Owned.Get<ISessionArchivePersistence>(), token), ct);

    public Task ActivateSkillAsync(SessionId sessionId, SkillId skill, MessageId? id = null, bool? resume = null, CancellationToken ct = default) =>
        RunAsync(token => Owned.Get<SessionSkillService>().ActivateAsync(sessionId, new SessionSkillRequest(skill, id, resume), token), ct);

    public Task MoveAsync(SessionMoveRequest request, CancellationToken ct = default) =>
        RunAsync(token => Owned.Get<SessionExecutionService>().MoveAsync(request, token), ct);

    public Task ClearRevertAsync(SessionId sessionId, CancellationToken ct = default) =>
        RunAsync(token => Owned.Get<SessionExecutionService>().ClearRevertAsync(sessionId, token), ct);

    public Task<SessionInboxItem> RequestCompactionAsync(SessionId sessionId, MessageId? id = null,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default) =>
        RunAsync(token => _engine.RequestCompactionAsync(sessionId, Owned.Stopping, id, delivery, token), ct);

    /// <summary>Explicit startup only, after the caller proves predecessor death and owns managed registration.
    /// Never called automatically by construction/CreateAsync or normal Session operations.</summary>
    public Task<SessionRecoveryReport> RecoverAfterConfirmedRestartAsync(int maxAttempts = 10) => RunAsync(async token =>
    {
        var active = _engine.ActiveSessionIds;
        var pending = await Jobs.PendingBackgroundAsync(token);
        var suspended = (await _sessionStore.ListSuspendedAsync(token)).Concat(pending
            .Where(item => item.Status == JobStatus.Running && item.Recovery is JobSubagentRecovery)
            .Select(item => ((JobSubagentRecovery)item.Recovery).ChildSessionId)).Where(id => !active.Contains(id)).ToHashSet();
        await Owned.Get<ShellToolJobs>().RecoverAfterConfirmedRestartAsync(suspended, token);
        return await Subagents.RecoverSuspendedAsync(maxAttempts);
    }, default);

    public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(SessionId sessionId, CancellationToken ct = default) => RunAsync(async token =>
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, token) ?? throw new SessionMutationNotFoundException(sessionId);
        await using var location = await Owned.Get<PermissionLocationMap>().TryAcquireLoadedAsync(session.Location, token);
        return location is null ? Array.Empty<PermissionRequest>() : await location.Permissions.ListAsync(sessionId, token);
    }, ct);

    public Task ReplyPermissionAsync(SessionId sessionId, PermissionId permissionId, PermissionReply reply, string? message = null, CancellationToken ct = default) => RunAsync(async token =>
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, token) ?? throw new SessionMutationNotFoundException(sessionId);
        await using var location = await Owned.Get<PermissionLocationMap>().TryAcquireLoadedAsync(session.Location, token)
            ?? throw new KeyNotFoundException("The permission Location is not loaded.");
        await location.Permissions.ReplyAsync(permissionId, sessionId, reply, message, token);
    }, ct);

    public Task<IReadOnlyList<FormInfo>> PendingFormsAsync(SessionId sessionId, CancellationToken ct = default) => RunAsync(async token =>
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, token) ?? throw new SessionMutationNotFoundException(sessionId);
        await using var location = await FormLocations.AcquireAsync(session.Location, loadedOnly: true, ct: token);
        return location is null ? Array.Empty<FormInfo>() : location.Forms.List(sessionId.Value);
    }, ct);

    public Task ReplyFormAsync(SessionId sessionId, FormId formId, FormAnswer answer, CancellationToken ct = default) => RunAsync(async token =>
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, token) ?? throw new SessionMutationNotFoundException(sessionId);
        await using var location = await FormLocations.AcquireAsync(session.Location, loadedOnly: true, ct: token);
        if (location is null || location.Forms.Get(formId).SessionId != sessionId.Value) throw new FormNotFoundException(formId);
        location.Forms.Reply(formId, answer);
    }, ct);

    public Task CancelFormAsync(SessionId sessionId, FormId formId, CancellationToken ct = default) => RunAsync(async token =>
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, token) ?? throw new SessionMutationNotFoundException(sessionId);
        await using var location = await FormLocations.AcquireAsync(session.Location, loadedOnly: true, ct: token);
        if (location is null || location.Forms.Get(formId).SessionId != sessionId.Value) throw new FormNotFoundException(formId);
        location.Forms.Cancel(formId);
    }, ct);

    private OwnedSdkHost Owned => _owned ?? throw new InvalidOperationException("These host operations belong to the embedding host for an injected client.");
    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => _owned is null ? action(ct) : _owned.RunAsync(action, ct);
    private Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct) => _owned is null ? action(ct) : _owned.RunAsync(action, ct);
}
