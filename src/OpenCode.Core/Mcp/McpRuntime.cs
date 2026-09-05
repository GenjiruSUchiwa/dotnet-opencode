namespace OpenCode.Core.Mcp;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCode.Core.Tools;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

public sealed record McpDiscoveredTool(string Server, bool CodeMode, McpClientTool Tool);
public sealed record McpDiscoveredPrompt(string Server, McpClientPrompt Prompt);
public sealed record McpServerGuidance(string Server, string Instructions, bool CodeMode);
public sealed record McpObservation(IReadOnlyList<McpServer> Servers, IReadOnlyList<McpDiscoveredTool> Tools,
    IReadOnlyList<McpDiscoveredPrompt> Prompts, McpResourceCatalog Resources, IReadOnlyList<McpServerGuidance> Guidance);

/// <summary>One Location owns this runtime and its stable tool transform. Construction performs no I/O.</summary>
public sealed partial class McpRuntime : IAsyncDisposable
{
    // tool/mcp.ts always declares output, using {} when the MCP server omitted its
    // outputSchema. Keep the registry's no-schema/no-output contract strict elsewhere.
    private static readonly JsonElement UnconstrainedOutput = JsonSerializer.Deserialize<JsonElement>("{}");
    private readonly string _directory;
    private readonly ToolRegistry _registry;
    private readonly IToolPermission _permission;
    private readonly IMcpElicitationForms? _forms;
    private readonly McpOAuthService? _oauth;
    private readonly ToolRegistration _registration;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private volatile ToolInfo[] _tools = [];
    private bool _closed;

    /// <summary>Complete, registry-flushed observation; null before discovery, during an update, or after a failed flush.</summary>
    public McpObservation? SettledObservation { get => Volatile.Read(ref _observation); private set => Volatile.Write(ref _observation, value); }
    private McpObservation? _observation;

    private sealed class Entry(McpServerConfig config)
    {
        public McpServerConfig Config = config;
        public McpStatus Status = new McpPendingStatus();
        public McpClient? Client;
        public McpElicitationConnection? Elicitation;
        public IReadOnlyList<McpClientTool> Tools = [];
        public IReadOnlyList<McpClientPrompt> Prompts = [];
        public IReadOnlyList<McpClientResource> Resources = [];
        public IReadOnlyList<McpClientResourceTemplate> Templates = [];
        public int CatalogChanged;
    }

    public McpRuntime(string directory, ToolRegistry registry, IToolPermission permission, IMcpElicitationForms? forms = null, McpOAuthService? oauth = null)
    {
        _directory = Path.GetFullPath(directory);
        _registry = registry;
        _permission = permission;
        _forms = forms;
        _oauth = oauth;
        _registration = registry.Transform(draft => { foreach (var tool in _tools) draft.Add(tool); });
    }

    /// <summary>Documents must be in config precedence order. Server definitions replace; timeout fields merge.</summary>
    public static McpConfiguration Configure(IEnumerable<McpConfiguration> documents)
    {
        var timeout = new McpTimeoutConfig();
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            timeout = Merge(timeout, document.Timeout);
            foreach (var server in document.Servers ?? new Dictionary<string, McpServerConfig>()) servers[server.Key] = server.Value;
        }
        return new(timeout, servers.ToDictionary(item => item.Key, item => item.Value switch
        {
            McpLocalConfig local => (McpServerConfig)(local with { Timeout = Merge(timeout, local.Timeout) }),
            McpRemoteConfig remote => remote with { Timeout = Merge(timeout, remote.Timeout) },
            _ => throw new NotSupportedException("Unknown MCP configuration")
        }, StringComparer.Ordinal));
    }

    public Task<McpObservation> ObserveAsync(McpConfiguration configuration, CancellationToken ct = default) =>
        UpdateAsync(async token =>
        {
            _configuration = Configure([configuration]);
            await ReconcileAsync(token).ConfigureAwait(true);
            await ApplyChangesAsync(token).ConfigureAwait(true);
        }, ct);

    private async Task StartAsync(string name, Entry entry, CancellationToken ct, bool force = false)
    {
        if (!force && entry.Config is (McpLocalConfig { Disabled: true } or McpRemoteConfig { Disabled: true }))
        {
            SetStatus(name, entry, new McpDisabledStatus());
            return;
        }
        SetStatus(name, entry, new McpPendingStatus());
        try
        {
            using var startup = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromMilliseconds(Timeout(entry.Config)?.Startup ?? 30_000));
            if (entry.Config is McpLocalConfig local)
            {
                if (local.Command.Count == 0) throw new ArgumentException("MCP command must not be empty.", nameof(entry));
                // SDK owns System.Diagnostics.Process and bounded shutdown. Its public stdio options
                // do not expose .NET 11 process injection; no shell or alternate runtime is inserted here.
                entry.Client = await ConnectAsync(name, entry, new StdioClientTransport(new()
                {
                    Name = name, Command = local.Command[0], Arguments = local.Command.Skip(1).ToArray(),
                    WorkingDirectory = Path.GetFullPath(local.Cwd ?? ".", _directory),
                    EnvironmentVariables = local.Environment?.ToDictionary(item => item.Key, item => (string?)item.Value),
                    InheritEnvironmentVariables = true
                }), startup.Token).ConfigureAwait(true);
            }
            else if (entry.Config is McpRemoteConfig remote)
            {
                var url = new UriBuilder(remote.Url);
                if (url.Scheme is not ("http" or "https")) throw new ArgumentException("MCP remote URL must use HTTP or HTTPS.", nameof(entry));
                var added = remote.CodeMode != false && !url.Query.TrimStart('?').Split('&').Any(part => Uri.UnescapeDataString(part.Split('=')[0]) == "codemode");
                if (added) url.Query = url.Query.TrimStart('?') + (url.Query.Length > 1 ? "&" : "") + "codemode=false";
                try { entry.Client = await OpenRemoteAsync(name, entry, remote, url.Uri, startup.Token).ConfigureAwait(true); }
                catch (HttpRequestException error) when (added && error.StatusCode == HttpStatusCode.NotFound)
                { entry.Client = await OpenRemoteAsync(name, entry, remote, new Uri(remote.Url), startup.Token).ConfigureAwait(true); }
            }
            var connected = entry.Client ?? throw new NotSupportedException("Unknown MCP transport.");
            foreach (var (method, kind) in new[]
            {
                ("notifications/tools/list_changed", McpChangeKind.Tools),
                ("notifications/prompts/list_changed", McpChangeKind.Prompts),
                ("notifications/resources/list_changed", McpChangeKind.Resources)
            })
            {
                connected.RegisterNotificationHandler(method, (_, _) =>
                {
                    if (ReferenceEquals(entry.Client, connected)) QueueChange(entry, kind);
                    return ValueTask.CompletedTask;
                });
            }
            // Never await a lifecycle callback on the SDK receive loop: disposal and listing can
            // need that same loop. One coalesced runtime worker takes the lifecycle gate instead.
            _worker ??= ProcessChangesAsync();
            _ = connected.Completion.ContinueWith(completion =>
            {
                _ = completion.Exception;
                if (ReferenceEquals(entry.Client, connected)) QueueChange(entry, McpChangeKind.Status);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await RefreshAsync(entry, ct).ConfigureAwait(true);
            SetStatus(name, entry, new McpConnectedStatus());
            CatalogChanged(name, McpChangeKind.Tools | McpChangeKind.Prompts | McpChangeKind.Resources);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAsync(name, entry).ConfigureAwait(true);
            SetStatus(name, entry, new McpPendingStatus());
            throw;
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            await StopAsync(name, entry).ConfigureAwait(true);
            SetStatus(name, entry, entry.Config is McpRemoteConfig { OAuth: not McpOAuthDisabled }
                && error is (McpOAuthRequiredException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                ? new McpNeedsAuthStatus() : new McpFailedStatus(error.Message));
        }
    }

    private async Task RefreshAsync(Entry entry, CancellationToken ct)
    {
        var connected = entry.Client!;
        try
        {
            if (connected.ServerCapabilities.Tools is not null)
            {
                using var catalog = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
                catalog.CancelAfter(TimeSpan.FromMilliseconds(Timeout(entry.Config)?.Catalog ?? 30_000));
                entry.Tools = (await connected.ListToolsAsync(cancellationToken: catalog.Token).ConfigureAwait(false)).ToArray();
            }
            if (connected.ServerCapabilities.Prompts is not null)
                entry.Prompts = await OptionalCatalogAsync(entry, "prompts", async token => await connected.ListPromptsAsync(cancellationToken: token).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (connected.ServerCapabilities.Resources is not null)
            {
                entry.Resources = await OptionalCatalogAsync(entry, "resources", async token => await connected.ListResourcesAsync(cancellationToken: token).ConfigureAwait(false), ct).ConfigureAwait(false);
                entry.Templates = await OptionalCatalogAsync(entry, "resource templates", async token => await connected.ListResourceTemplatesAsync(cancellationToken: token).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            Interlocked.Or(ref entry.CatalogChanged, (int)(McpChangeKind.Tools | McpChangeKind.Prompts | McpChangeKind.Resources));
            throw;
        }
    }

    private async Task<IReadOnlyList<T>> OptionalCatalogAsync<T>(Entry entry, string catalog,
        Func<CancellationToken, Task<IList<T>>> list, CancellationToken ct)
    {
        // TS treats these three catalogs independently; their failures must not discard tools.
        using var timeout = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Timeout(entry.Config)?.Catalog ?? 30_000));
        try { return (await list(timeout.Token).ConfigureAwait(false)).ToArray(); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("MCP {0} discovery failed: {1}", catalog, error.Message);
            return [];
        }
    }

    private async Task<McpClient> ConnectAsync(string name, Entry entry, IClientTransport transport, CancellationToken ct)
    {
        var elicitation = _forms is null ? null : new McpElicitationConnection(_forms, name, _shutdown.Token);
        try
        {
            var client = await McpClient.CreateAsync(transport, new()
            {
                ClientInfo = new() { Name = "opencode", Version = "dotnet" },
#pragma warning disable MCP9005 // Preserve the source's roots support for existing MCP servers.
                Capabilities = new() { Roots = new(), Elicitation = elicitation is null ? null : new() { Form = new(), Url = new() } },
                Handlers = new()
                {
                    RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
                    { Roots = [new Root { Uri = new Uri(_directory + Path.DirectorySeparatorChar).AbsoluteUri }] }),
                    ElicitationHandler = elicitation is null ? null : elicitation.ElicitAsync,
                    NotificationHandlers = elicitation is null ? null :
                    [new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ElicitationCompleteNotification, elicitation.CompleteAsync)]
                }
#pragma warning restore MCP9005
            }, cancellationToken: ct).ConfigureAwait(true);
            entry.Elicitation = elicitation;
            return client;
        }
        catch
        {
            try { if (elicitation is not null) await elicitation.DisposeAsync().ConfigureAwait(true); }
            finally { if (transport is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(true); }
            throw;
        }
    }

    private async Task<McpClient> OpenRemoteAsync(string name, Entry entry, McpRemoteConfig remote, Uri uri, CancellationToken ct)
    {
        var oauth = _oauth is null ? null : await _oauth.ConnectionOptionsAsync(name, remote, ct).ConfigureAwait(true);
        return await ConnectAsync(name, entry, new HttpClientTransport(new()
        {
            Name = name, Endpoint = uri, TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = remote.Headers?.ToDictionary(item => item.Key, item => item.Value),
            OAuth = oauth
        }), ct).ConfigureAwait(true);
    }

    private ToolInfo Registration(string server, Entry entry, McpClientTool tool)
    {
        var input = JsonNode.Parse(tool.JsonSchema.GetRawText())!.AsObject();
        input["type"] = "object";
        input["properties"] ??= new JsonObject();
        input["additionalProperties"] = false;
        return ToolInfo.FromJson(tool.Name, tool.Description ?? "", JsonSerializer.SerializeToElement(input), async (args, context, ct) =>
        {
            try { await _permission.AssertAsync(ToolInfo.EffectiveName(tool.Name, ToolInfo.NormalizedName(server)), ["*"], ["*"], context, null, ct).ConfigureAwait(true); }
            catch (PermissionCorrectedException error) { throw new ToolExecutionException(error.Feedback, error); }
            using var execution = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
            CallToolResult result;
            try
            {
                // A captured registry definition resolves the current connection, as TS callTool does.
                // Reconnect must not strand it on the disposed McpClientTool from discovery.
                var live = await ConnectedAsync(server, ct).ConfigureAwait(true);
                execution.CancelAfter(TimeSpan.FromMilliseconds(Timeout(live.Config)?.Execution ?? 43_200_000));
                result = await live.Client.CallToolAsync(tool.Name,
                    args.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()),
                    options: new() { Meta = new JsonObject { ["sessionID"] = context.SessionId.Value } }, cancellationToken: execution.Token).ConfigureAwait(true);
            }
            catch (Exception error) when (error is ModelContextProtocol.McpException or HttpRequestException or IOException or McpServerNotFoundException or McpNotConnectedException or McpOAuthRequiredException)
            { throw new ToolExecutionException(error.Message, error); }
            catch (OperationCanceledException error) when (!ct.IsCancellationRequested && execution.IsCancellationRequested)
            { throw new ToolExecutionException("MCP tool execution timed out", error); }
            var content = result.Content.SelectMany(ConvertContent).ToArray();
            var text = string.Join("\n", content.OfType<ToolTextContent>().Select(part => part.Text));
            if (result.IsError == true) throw new ToolExecutionException(string.IsNullOrWhiteSpace(text) ? "MCP tool returned an error" : text.Trim());
            return new ToolExecutionResult { Content = content, Output = result.StructuredContent is { } structured ? JsonSerializer.SerializeToElement(structured) : text.Length == 0 ? null : text };
        }, tool.ReturnJsonSchema ?? UnconstrainedOutput, new(Namespace: ToolInfo.NormalizedName(server), CodeMode: CodeMode(entry.Config)));
    }

    private static IEnumerable<ToolContent> ConvertContent(ContentBlock block) => block switch
    {
        TextContentBlock text => [new ToolTextContent(text.Text)],
        ImageContentBlock image => [new ToolFileContent($"data:{image.MimeType};base64,{Encoding.UTF8.GetString(image.Data.Span)}", image.MimeType)],
        AudioContentBlock audio => [new ToolFileContent($"data:{audio.MimeType};base64,{Encoding.UTF8.GetString(audio.Data.Span)}", audio.MimeType)],
        ResourceLinkBlock link => [new ToolTextContent(link.Uri)],
        EmbeddedResourceBlock { Resource: TextResourceContents text } => [new ToolTextContent(text.Text)],
        EmbeddedResourceBlock { Resource: BlobResourceContents blob } when blob.MimeType is not null => [new ToolFileContent($"data:{blob.MimeType};base64,{Encoding.UTF8.GetString(blob.Blob.Span)}", blob.MimeType)],
        EmbeddedResourceBlock resource => [new ToolTextContent(resource.Resource.Uri)],
        _ => []
    };

    private McpObservation Snapshot() => new(
        _entries.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => ServerInfo(item.Key, item.Value)).ToArray(),
        _entries.SelectMany(item => item.Value.Tools.Select(tool => new McpDiscoveredTool(item.Key, CodeMode(item.Value.Config), tool)))
            .OrderBy(item => item.Server, StringComparer.Ordinal).ThenBy(item => item.Tool.Name, StringComparer.Ordinal).ToArray(),
        _entries.SelectMany(item => item.Value.Prompts.Select(prompt => new McpDiscoveredPrompt(item.Key, prompt)))
            .OrderBy(item => item.Server, StringComparer.Ordinal).ThenBy(item => item.Prompt.Name, StringComparer.Ordinal).ToArray(),
        new(_entries.SelectMany(item => item.Value.Resources.Select(resource => new McpResource(item.Key, resource.Name, resource.Uri, resource.Description, resource.MimeType)))
                .OrderBy(item => item.Server, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Uri, StringComparer.Ordinal).ToArray(),
            _entries.SelectMany(item => item.Value.Templates.Select(template => new McpResourceTemplate(item.Key, template.Name, template.UriTemplate, template.Description, template.MimeType)))
                .OrderBy(item => item.Server, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.UriTemplate, StringComparer.Ordinal).ToArray()),
        _entries.Where(item => !string.IsNullOrWhiteSpace(item.Value.Client?.ServerInstructions))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new McpServerGuidance(item.Key, item.Value.Client!.ServerInstructions!.Trim(), CodeMode(item.Value.Config))).ToArray());

    public async Task<GetPromptResult> PromptAsync(string server, string name, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken ct = default)
    {
        var entry = await ConnectedAsync(server, ct).ConfigureAwait(false);
        using var timeout = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Timeout(entry.Config)?.Execution ?? 43_200_000));
        return await entry.Client.GetPromptAsync(name, arguments, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    public async Task<ReadResourceResult> ReadResourceAsync(string server, string uri, CancellationToken ct = default)
    {
        var entry = await ConnectedAsync(server, ct).ConfigureAwait(false);
        using var timeout = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Timeout(entry.Config)?.Execution ?? 43_200_000));
        return await entry.Client.ReadResourceAsync(uri, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private static bool CodeMode(McpServerConfig config) => config switch { McpLocalConfig local => local.CodeMode != false, McpRemoteConfig remote => remote.CodeMode != false, _ => true };
    private static McpTimeoutConfig? Timeout(McpServerConfig config) => config switch { McpLocalConfig local => local.Timeout, McpRemoteConfig remote => remote.Timeout, _ => null };
    private static McpTimeoutConfig Merge(McpTimeoutConfig defaults, McpTimeoutConfig? value) => new(value?.Startup ?? defaults.Startup, value?.Catalog ?? defaults.Catalog, value?.Execution ?? defaults.Execution);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(true);
        _signals.Writer.TryComplete();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            if (_closed) return;
            _closed = true;
            await _registration.DisposeAsync().ConfigureAwait(true);
            foreach (var item in _entries) await StopAsync(item.Key, item.Value).ConfigureAwait(true);
            _entries.Clear();
            _servers = [];
            SettledObservation = null;
        }
        finally { _gate.Release(); }
        if (_worker is { } worker) await worker.ConfigureAwait(true);
    }
}
