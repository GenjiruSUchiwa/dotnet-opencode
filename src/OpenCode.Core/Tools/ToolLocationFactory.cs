namespace OpenCode.Core.Tools;

using System.Runtime.CompilerServices;
using OpenCode.Core.Agent;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Formatting;
using OpenCode.Core.Instructions;
using OpenCode.Core.Locations;
using OpenCode.Core.Mcp;
using OpenCode.Core.Forms;
using OpenCode.Core.Session.Subagents;
using OpenCode.Core.Shell;
using OpenCode.Core.Permissions;
using OpenCode.Core.Plugins;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Core.WebSearch;
using OpenCode.Schema;

/// <param name="ShellEnvironment">Bind the host's SessionEnvironment.Get. Null inherits the host environment;
/// an empty result replaces it with an empty map. This factory accepts implicit-local placement only.</param>
/// <param name="McpForms">The host's Location-owned Forms adapter. The host retains ownership of this service.</param>
/// <param name="McpCreated">Subscribe synchronously before any observation. Return a non-null subscription;
/// it is disposed after MCP shutdown and before the registry. Do not observe the runtime or reenter the Location map here.
/// A callback that throws must clean up its own incomplete subscription.</param>
/// <param name="WebSearchReady">Read/configure the existing WebSearch runtime after provider plugins initialize.
/// Must not reacquire this Location or allocate a runtime/provider. Called again before model snapshots.</param>
public sealed record LocalToolOptions(
    string Home,
    string RipgrepExecutable,
    LoadReadInstructions LoadReadInstructions,
    IToolFileFormatter? Formatter = null,
    IToolExecutionHooks? Hooks = null,
    IReadOnlyList<string>? ProjectMarkers = null,
    int MaximumMutationBytes = 20 * 1024 * 1024,
    string? FormatterBin = null,
    WebFetchTransport? WebFetch = null,
    string? ShellExecutable = null,
    string? ShellOutputDirectory = null,
    Func<SessionId, IReadOnlyDictionary<string, string>?>? ShellEnvironment = null,
    IMcpElicitationForms? McpForms = null,
    Func<LocationInfo, McpRuntime, IDisposable>? McpCreated = null,
    McpOAuthService? McpOAuth = null,
    FormService? QuestionForms = null,
    Func<SessionSubagents>? Subagents = null,
    Func<ShellRuntime>? ShellRuntime = null,
    Func<IShellToolJobs>? ShellJobs = null,
    IReadOnlyList<NativePluginDefinition>? Plugins = null,
    Action<PluginId?>? PluginChanged = null,
    Func<CancellationToken, Task<WebSearchRuntime>>? WebSearchReady = null);

/// <summary>Concrete local Location composition. Install this factory in the SAME PermissionLocationMap
/// used by Server. It registers supported modules but never advertises tools to a model or starts execution.</summary>
public sealed class ToolLocationFactory(
    SessionStore sessions,
    Func<LocationRef, CancellationToken, ValueTask<LocationInfo>> resolveLocation,
    IPermissionGrantStore grants,
    Func<LocationInfo, LocalToolOptions> local,
    Func<LocationInfo, IPermissionEvaluationHook?>? permissionHooks = null) : IPermissionLocationFactory
{
    private readonly ConditionalWeakTable<PermissionService, ToolLocationState> _loaded = new();

    public async ValueTask<PermissionLocationScope> CreateAsync(LocationRef location, CancellationToken ct)
    {
        if (location.WorkspaceId is not null) throw new NotSupportedException("Local builtins do not support explicit workspace placement.");
        var info = await resolveLocation(location, ct).ConfigureAwait(true);
        if (PermissionLocationMap.Canonical(new(info.Directory, info.WorkspaceId)) != PermissionLocationMap.Canonical(location))
            throw new InvalidOperationException("Tool Location resolution must preserve the authoritative Location key.");
        var options = local(info);
        ArgumentNullException.ThrowIfNull(options.LoadReadInstructions, nameof(local));
        if (!Path.IsPathFullyQualified(options.RipgrepExecutable)) throw new ArgumentException("The host must supply an absolute ripgrep executable.", nameof(local));
        var files = new LocalToolLocation(info.Directory, info.Project.Directory, options.Home, options.ProjectMarkers);
        var mutation = new LocalFileMutation(options.Formatter ?? new LocalFormatter(info.Directory, info.Project.Directory, options.FormatterBin, sessions.Clock), options.MaximumMutationBytes);
        var rules = new StorePermissionRules(sessions, location,
            (agent, cancellation) => new ValueTask<AgentInfo?>(AgentCatalog.ResolveAsync(info.Directory, agent, cancellation)));
        var permission = new PermissionService(info.Project.Id.Value, rules, grants, permissionHooks?.Invoke(info));
        var plugins = new NativePluginHost(info, options.PluginChanged, options.Hooks);
        var registry = new ToolRegistry(hooks: plugins, clock: sessions.Clock);
        plugins.Attach(registry);
        McpRuntime? mcp = null;
        IDisposable? mcpLifetime = null;
        WebSearchToolBinding? websearch = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var policy = new ToolFilePolicy(files, permission);
            var ripgrep = new RipgrepProcess(options.RipgrepExecutable, sessions.Clock);
            // One stable builtin transform; later plugin/MCP transforms keep normal source precedence.
            var tools = new[]
            {
                new ReadTool(policy, new ReadInstructionDiscovery(files, options.LoadReadInstructions)).Create(),
                new GrepTool(policy, ripgrep).Create(),
                new GlobTool(policy, ripgrep).Create(),
                new WriteTool(policy, mutation).Create(),
                new EditTool(policy, mutation).Create(),
                new ShellTool(new LocalShellPolicy(files, permission, options.ShellExecutable),
                    runtime: options.ShellRuntime, jobs: options.ShellJobs?.Invoke()).Create(),
                new SkillTool(permission, cancellation => InstructionCatalog.ListSkillsAsync(info.Directory, cancellation)).Create()
            };
            var webfetch = options.WebFetch is null ? null : new WebFetchTool(options.WebFetch, permission, sessions.Clock).Create();
            var question = options.QuestionForms is null ? null : new QuestionTool(options.QuestionForms, permission).Create();
            var subagent = options.Subagents is null ? null : new SubagentTool(options.Subagents(), permission).Create();
            var byName = tools.Concat(new[] { webfetch, question, subagent }.OfType<ToolInfo>()).ToDictionary(tool => tool.Id);
            // Preserve the supported subset of PluginInternal.pre's producer order.
            var definitions = new[] { "edit", "glob", "grep", "question", "read", "shell", "skill", "subagent", "webfetch", "websearch", "write" }
                .Where(name => byName.ContainsKey(name) || name == WebSearchTool.Name && options.WebSearchReady is not null)
                .Select(name => new NativePluginDefinition(PluginId.FromExisting("opencode.tool." + name), "native", (scope, _) =>
                {
                    if (name == WebSearchTool.Name)
                    {
                        var binding = scope.Own(new WebSearchToolBinding(options.WebSearchReady!, permission, options.QuestionForms, registry, sessions.Clock, scope.Lifetime));
                        scope.TransformTools(binding.Apply);
                        websearch = binding;
                    }
                    else scope.TransformTools(draft => draft.Add(byName[name]));
                    return ValueTask.CompletedTask;
                }))
                .Concat(options.Plugins ?? []).ToArray();
            await plugins.ActivateAsync(definitions, ct).ConfigureAwait(true);
            // Provider initialization comes from options.Plugins; only now may readiness
            // borrow their runtime. The builtin transform keeps its original precedence.
            if (websearch is not null) await websearch.RefreshAsync(ct).ConfigureAwait(true);
            if (registry.RegistrationErrors.Count != 0)
                throw new InvalidOperationException(string.Join("\n", registry.RegistrationErrors.Select(error => error.Message)));
            // McpRuntime installs exactly one transform. ObserveAsync refreshes its source and reloads it.
            mcp = new McpRuntime(info.Directory, registry, permission, options.McpForms, options.McpOAuth);
            // Attach the host bridge before exposing a runtime that can be observed. The returned
            // subscription belongs to this Location, not to a request or a second runtime registry.
            mcpLifetime = options.McpCreated is { } created
                ? created(info, mcp) ?? throw new InvalidOperationException("McpCreated must return an owned subscription.")
                : null;
            ct.ThrowIfCancellationRequested();
            var state = new ToolLocationState(registry, rules, mcp, mcpLifetime, plugins,
                token => websearch?.RefreshAsync(token) ?? Task.CompletedTask);
            _loaded.Add(permission, state);
            return new(info, permission, state);
        }
        catch
        {
            try { if (mcp is not null) await mcp.DisposeAsync().ConfigureAwait(true); }
            finally
            {
                try { mcpLifetime?.Dispose(); }
                finally
                {
                    try { try { await plugins.DisposeAsync().ConfigureAwait(true); } finally { await registry.DisposeAsync().ConfigureAwait(true); } }
                    finally { await permission.DisposeAsync().ConfigureAwait(true); }
                }
            }
            throw;
        }
    }

    public async ValueTask<ToolLocationLease> AcquireAsync(PermissionLocationMap locations, LocationRef location, CancellationToken ct = default)
    {
        var lease = await locations.AcquireAsync(location, ct).ConfigureAwait(true);
        try
        {
            if (!_loaded.TryGetValue(lease.Permissions, out var state))
                throw new NotSupportedException("This permission Location was not constructed by this ToolLocationFactory.");
            await state.RefreshWebSearch(ct).ConfigureAwait(true);
            return new(lease, state.Registry, state.Rules, state.Mcp, state.Plugins, state.RefreshWebSearch);
        }
        catch { await lease.DisposeAsync().ConfigureAwait(true); throw; }
    }

    private sealed record ToolLocationState(ToolRegistry Registry, IPermissionRuleSource Rules, McpRuntime Mcp, IDisposable? McpLifetime,
        NativePluginHost Plugins, Func<CancellationToken, Task> RefreshWebSearch) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await Mcp.DisposeAsync().ConfigureAwait(true); }
            finally
            {
                try { McpLifetime?.Dispose(); }
                finally { try { await Plugins.DisposeAsync().ConfigureAwait(true); } finally { await Registry.DisposeAsync().ConfigureAwait(true); } }
            }
        }
    }
}

/// <summary>Keep this lease alive for the complete model request and all calls using its captured snapshot.
/// The map owns the registry and permission service; request disposal releases only the shared Location lease.</summary>
public sealed class ToolLocationLease : IAsyncDisposable
{
    private readonly PermissionLocationLease _lease;
    private readonly IPermissionRuleSource _rules;
    private readonly Func<CancellationToken, Task> _refreshWebSearch;
    private int _disposed;
    public LocationInfo Location => _lease.Location;
    public PermissionService Permissions => _lease.Permissions;
    public ToolRegistry Registry { get; }
    /// <summary>The Location's shared runtime. Await ObserveAsync before capturing tools and reuse its returned observation for instructions.</summary>
    public McpRuntime Mcp { get; }
    public NativePluginHost Plugins { get; }

    internal ToolLocationLease(PermissionLocationLease lease, ToolRegistry registry, IPermissionRuleSource rules, McpRuntime mcp,
        NativePluginHost plugins, Func<CancellationToken, Task> refreshWebSearch)
    { _lease = lease; Registry = registry; _rules = rules; Mcp = mcp; Plugins = plugins; _refreshWebSearch = refreshWebSearch; }

    public async Task<ToolSnapshot> SnapshotAsync(SessionId session, AgentId? agent = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Permissions.IsDisposed, this);
        if (Mcp.SettledObservation is null)
            throw new InvalidOperationException("Await the shared MCP runtime observation before capturing a tool snapshot.");
        var permissions = await _rules.GetAsync(session, agent, ct).ConfigureAwait(true);
        await _refreshWebSearch(ct).ConfigureAwait(true);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Permissions.IsDisposed, this);
        return Registry.Snapshot(permissions);
    }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? _lease.DisposeAsync() : ValueTask.CompletedTask;
}
