namespace OpenCode.Core.Session;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenCode.Core.Agent;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Llm;
using OpenCode.Schema;

/// <summary>Source title generation: auxiliary streams, no prompt admission or Session execution claim.</summary>
public sealed class SessionTitleService : IAsyncDisposable
{
    private readonly SessionStore _sessions;
    private readonly ProviderResolver _providers;
    private readonly SessionQueries _queries;
    private readonly SessionTitlePersistence _persistence;
    private readonly CancellationTokenSource _lifetime;
    private readonly SessionRequestIdentity? _identity;
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, Task> _automatic = [];
    private readonly HashSet<Task> _pending = [];
    private bool _closed;

    public SessionTitleService(IDatabase database, SessionStore sessions, ProviderResolver providers, CancellationToken lifetime,
        SessionRequestIdentity? identity = null)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Title generation requires a cancellable host lifetime.", nameof(lifetime));
        _sessions = sessions; _providers = providers;
        _queries = new SessionQueries(database);
        _persistence = new SessionTitlePersistence(database);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _identity = identity;
    }

    public static bool IsUntitled(SessionInfo session) => session.Title is null || session.Title ==
        "New session - " + session.Time.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Call only after promotion, for an untitled top-level Session. Coalesces automatic work while in flight.</summary>
    public void Schedule(SessionId sessionId)
    {
        lock (_gate)
        {
            if (_closed || _lifetime.IsCancellationRequested || _automatic.ContainsKey(sessionId)) return;
            var task = RunAsync(sessionId, default);
            _automatic.Add(sessionId, task);
            _pending.Add(task);
            _ = ObserveAsync(task, sessionId);
        }
    }

    /// <summary>Explicit regeneration, including the Server's empty-rename operation. Missing Sessions/input are no-ops.</summary>
    public Task GenerateAsync(SessionId sessionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var task = RunAsync(sessionId, ct);
            _pending.Add(task);
            _ = ObserveAsync(task, null);
            return task;
        }
    }

    private async Task RunAsync(SessionId sessionId, CancellationToken ct)
    {
        // Defers synchronous catalog/filesystem work too; Schedule never delays the primary model request.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = cancellation.Token;
        var session = await _sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (session is null) return;
        var first = await _persistence.FirstUserAsync(sessionId, token).ConfigureAwait(false);
        if (first is null) return;
        var text = first.Text;
        if (!IsUntitled(session))
        {
            try
            {
                var original = "Original request:\n" + first.Text[..Math.Min(2000, first.Text.Length)];
                var recent = string.Join("\n\n", (await _queries.ContextAsync(sessionId, token).ConfigureAwait(false)).SelectMany(message =>
                {
                    if (message is UserMessage user && user.Id != first.Id) return new[] { "User: " + SessionText.Trim(user.Text) };
                    if (message is not AssistantMessage assistant) return [];
                    var content = string.Join('\n', assistant.Content.OfType<AssistantTextContent>().Select(part => SessionText.Trim(part.Text)).Where(part => part.Length > 0));
                    return content.Length == 0 ? [] : new[] { "Assistant: " + content };
                }));
                var prefix = original + "\n\nRecent conversation:\n";
                text = recent.Length == 0 ? original : prefix + recent[Math.Max(0, recent.Length - (8000 - prefix.Length))..];
            }
            catch (SessionMessageReadException) { text = first.Text; }
        }

        // Title selection intentionally does not acquire tools, MCP or instruction epochs.
        // AgentCatalog still rejects configured plugin producers rather than pretending their hooks ran.
        var agent = await AgentCatalog.ResolveAsync(session.Location.Directory, AgentId.FromExisting("title"), token).ConfigureAwait(false);
        if (agent is null) return;
        if (session.Location.WorkspaceId is not null) throw new NotSupportedException("Title generation requires local Location model routing.");
        var catalog = await _providers.ReadCatalogAsync(session.Location.Directory, token).ConfigureAwait(false);
        var primaryRef = session.Model ?? catalog.DefaultSelection;
        var primaryInfo = primaryRef is null ? null : catalog.Models.FirstOrDefault(model => model.ProviderId == primaryRef.ProviderId && model.Id == primaryRef.Id);
        var primary = await ResolveAsync(session, primaryInfo, primaryRef, token).ConfigureAwait(false);
        CatalogModelInfo? info = null;
        if (agent.Model is { } configured)
            info = catalog.Models.FirstOrDefault(model => model.ProviderId == configured.ProviderId && model.Id == configured.Id);
        else if (primary is not null)
        {
            var models = catalog.Models.Where(model => model.ProviderId == primary.Selection.ProviderId && model.Enabled && model.Status == "active" &&
                model.Capabilities?.Input?.Any(item => item.StartsWith("text", StringComparison.Ordinal)) == true &&
                model.Capabilities.Output?.Any(item => item.StartsWith("text", StringComparison.Ordinal)) == true).OrderByDescending(model => model.Time.Released).ToArray();
            info = new[] { "gpt-luna", "gemini-flash-lite", "gemini-flash", "claude-haiku" }
                .Select(family => models.FirstOrDefault(model => model.Family == family)).FirstOrDefault(model => model is not null);
        }
        var variant = agent.Model?.Variant ?? new[] { "none", "minimal", "low" }.FirstOrDefault(id => info?.Variants.Any(item => item.Id == id) == true);
        var preferred = info is null ? null : await ResolveAsync(session, info, new ModelRef(info.ProviderId, info.Id, variant), token).ConfigureAwait(false);
        var selected = preferred ?? primary;
        if (selected is null) return;
        var selectedInfo = preferred is null ? primaryInfo! : info!;
        var title = await AttemptAsync(session, agent, text, selected, selectedInfo, token).ConfigureAwait(false);
        if (title is null && primary is not null && selected.Selection != primary.Selection)
            title = await AttemptAsync(session, agent, text, primary, primaryInfo!, token).ConfigureAwait(false);
        if (title is null) return;
        title = title.Length <= 100 ? title : title[..97] + "...";
        var expected = await _persistence.ExpectedSequenceAsync(sessionId, token).ConfigureAwait(false);
        var current = await _sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (current is null || current.Title != session.Title || current.Title == title) return;
        await _persistence.RenameAsync(sessionId, session.Title, title, expected, token).ConfigureAwait(false);
    }

    private async Task<ResolvedModel?> ResolveAsync(SessionInfo session, CatalogModelInfo? info, ModelRef? selection, CancellationToken ct)
    {
        if (info is null || selection is null || !info.Available || !info.TransportSupported) return null;
        if (selection.Variant is not (null or "default") && !info.Variants.Any(item => item.Id == selection.Variant)) return null;
        try { return await _providers.ResolveAsync(ct: ct, directory: session.Location.Directory, sessionModel: selection, sessionId: session.Id.Value).ConfigureAwait(false); }
        catch (LlmException) { return null; }
    }

    private async Task<string?> AttemptAsync(SessionInfo session, AgentInfo agent, string text, ResolvedModel model,
        CatalogModelInfo info, CancellationToken ct)
    {
        SessionExecutionEngine.RequireToolContract(ConfigLoader.LoadConfig(directory: session.Location.Directory), model.Selection, agent);
        var request = new LlmRequest(model.ModelId, [new LlmMessage(LlmRole.User, [new LlmContent.Text(text)])])
        {
            PromptCacheKey = SessionRequestIdentity.PromptCacheKey(session),
            System = agent.System is { Length: > 0 } system ? [new LlmSystemPart(system)] : [],
            Tools = [], ToolChoice = new LlmToolChoice.None(),
            Http = new LlmHttpOptions
            {
                Headers = SessionRequestIdentity.Headers(session, agent.Request.Headers, _identity),
                Body = agent.Request.Body.ToImmutableDictionary(StringComparer.Ordinal)
            }
        };
        var chunks = new StringBuilder();
        var failed = false;
        var terminal = false;
        TokenUsageInfo? tokens = null;
        double cost = 0;
        try
        {
            // One physical auxiliary stream. The caller may make only the source's distinct-primary fallback.
            await foreach (var item in model.Client.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (item)
                {
                    case LlmEvent.TextDelta delta: chunks.Append(delta.Text); break;
                    case LlmEvent.ProviderError: terminal = true; failed = true; break;
                    case LlmEvent.Finish: terminal = true; break;
                    case LlmEvent.StepFinish finish:
                        var usage = SessionUsage.Tokens(finish.Usage);
                        tokens = tokens is null ? usage : SessionUsage.Add(tokens, usage);
                        cost += SessionUsage.Cost(info.Cost, usage).Amount;
                        break;
                    case LlmEvent.ToolCall or LlmEvent.ToolInputStart or LlmEvent.ToolResult or LlmEvent.ToolError:
                        throw new LlmException(new LlmFailure.Unsupported("Title generation does not invoke tools."));
                }
            }
            ct.ThrowIfCancellationRequested();
            if (!terminal) throw new LlmException(new LlmFailure.InvalidProviderOutput("The provider response ended unexpectedly.", true));
        }
        catch (LlmException) { failed = true; }
        finally
        {
            if (tokens is not null)
            {
                using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(15), _sessions.Clock);
                await _persistence.UsageAsync(session.Id, Money.FromExisting(cost), tokens, publication.Token).ConfigureAwait(false);
            }
        }
        return failed ? null : chunks.ToString().Split('\n').Select(SessionText.Trim).FirstOrDefault(line => line.Length > 0);
    }

    private async Task ObserveAsync(Task task, SessionId? automatic)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Trace.TraceWarning("Title generation failed: {0}", error.Message); }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(task);
                if (automatic is { } id && _automatic.GetValueOrDefault(id) == task) _automatic.Remove(id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate) { if (_closed) return; _closed = true; pending = _pending.ToArray(); }
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
        finally { _lifetime.Dispose(); }
    }

}
