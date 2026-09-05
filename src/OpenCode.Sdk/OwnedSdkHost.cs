namespace OpenCode.Sdk;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Formatting;
using OpenCode.Core.Jobs;
using OpenCode.Core.Llm;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Core.Pty;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Archive;
using OpenCode.Core.Session.Skills;
using OpenCode.Core.Session.Subagents;
using OpenCode.Core.Session.Transfer;
using OpenCode.Core.Shell;
using OpenCode.Core.Shell.Jobs;
using OpenCode.Core.Snapshot;
using OpenCode.Core.Tools;
using OpenCode.Core.Worktrees;
using OpenCode.Core.Config;
using OpenCode.Schema;
using OpenCode.Server.Integrations;
using OpenCode.Server.Services;
using OpenCode.Server.Shell;

internal sealed class OwnedSdkHost : IAsyncDisposable
{
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void Start() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
        public void Complete() => _stopped.Cancel();
        public void Dispose() { _started.Dispose(); _stopping.Dispose(); _stopped.Dispose(); }
    }

    internal sealed class Operation(OwnedSdkHost host, CancellationToken caller) : IDisposable
    {
        private readonly CancellationTokenSource _token = CancellationTokenSource.CreateLinkedTokenSource(caller, host.Stopping);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token => _token.Token;
        public void Dispose()
        {
            lock (host._gate)
            {
                if (!host._operations.Remove(this)) return;
                _token.Dispose();
                Done.TrySetResult();
            }
        }
    }

    private readonly Lifetime _lifetime = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Operation> _operations = [];
    private readonly ServiceProvider _services;
    private readonly IHostedService[] _hosted;
    private Task? _closing;
    internal CancellationToken Stopping => _lifetime.ApplicationStopping;
    internal T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    internal OwnedSdkHost(SdkHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Clock);
        ArgumentNullException.ThrowIfNull(options.Identity);
        if (string.IsNullOrWhiteSpace(options.Identity.ClientName) || string.IsNullOrWhiteSpace(options.Identity.UserAgent) ||
            options.Identity.ClientName.Any(char.IsControl) || options.Identity.UserAgent.Any(char.IsControl))
            throw new ArgumentException("SDK host identity must contain valid client and user-agent values.", nameof(options));
        var services = new ServiceCollection();
        services.AddSingleton(options.Clock);
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(_lifetime);
        services.AddSingleton<IEventFeedService, EventFeedService>();
        services.AddSingleton<HttpClient>();
        // Keep schema/path ownership here; Core's EF contexts are operation-local
        // and borrow this owner's connections. No EF migration service is composed.
        services.AddSingleton<IDatabase>(_ => new SqliteDatabase(options.DatabasePath, clock: options.Clock));
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<ProviderResolver>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<WebFetchTransport>();
        services.AddSingleton<SessionQueries>();
        services.AddSingleton<SessionArchiveService>();
        services.AddSingleton<ISessionArchivePersistence, SessionArchivePersistence>();
        services.AddSingleton<ISessionSkillPublisher, SessionSkillPublisher>();
        services.AddSingleton(provider => new SessionSkillService(provider.GetRequiredService<SessionStore>(),
            provider.GetRequiredService<ISessionSkillPublisher>(), provider.GetRequiredService<SessionExecutionEngine>(), Stopping));
        services.AddSingleton<SessionInstructionEntries>();
        services.AddSingleton<SessionEnvironment>();
        services.AddSingleton<SessionMutations>();
        services.AddSingleton<SessionMovement>();
        services.AddSingleton<SessionTransfer>();
        services.AddSingleton<WorktreeService>();
        services.AddSingleton<SessionSnapshotLocations>();
        services.AddSingleton<SessionRevertOperations>();
        services.AddSingleton<ISessionShellLifecycle, SessionShellLifecycle>();
        services.AddSingleton<IJobBackgroundStore, JobBackgroundStore>();
        services.AddSingleton(provider => new JobRuntime(provider.GetRequiredService<IJobBackgroundStore>(), Stopping, options.Clock));
        services.AddSingleton(provider => new SessionTitleService(provider.GetRequiredService<IDatabase>(), provider.GetRequiredService<SessionStore>(),
            provider.GetRequiredService<ProviderResolver>(), Stopping, options.Identity));
        services.AddSingleton(provider => new SessionPromptPreparation(provider.GetRequiredService<SessionStore>(), options.NormalizeImage,
            (session, ct) => SessionRevertOperations.CommitPreparedAsync(provider.GetRequiredService<IDatabase>(), session, ct)));
        if (options.PermissionGrants is { } grants) services.AddSingleton(grants);
        else
        {
            services.AddSingleton<SqlitePermissionGrantStore>();
            services.AddSingleton<IPermissionGrantStore>(provider => provider.GetRequiredService<SqlitePermissionGrantStore>());
        }
        ServiceProvider? host = null;
        services.AddLocalToolLocations(location => Tools(location, host!, options), options.PermissionHooks,
            async location =>
            {
                var provider = host ?? throw new InvalidOperationException("The SDK host is not constructed.");
                var reference = new LocationRef(location.Directory, location.WorkspaceId);
                provider.GetRequiredService<FormLocationServices>().Invalidate(reference);
                await provider.GetRequiredService<IntegrationHostService>().InvalidateAsync(reference);
                await provider.GetRequiredService<ShellLocationServices>().InvalidateAsync(reference);
            });
        services.AddSingleton<FormLocationServices>();
        services.AddHostedService<FormLocationServices>(provider => provider.GetRequiredService<FormLocationServices>());
        services.AddIntegrationServices();
        services.AddShellServices(new ShellHostOptions(
            (location, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(PtyShellSelection.Resolve(location)); },
            async (location, ct) =>
            {
                await using var snapshot = await host!.GetRequiredService<CommandHostService>().AcquireAsync(new(location.Directory, location.WorkspaceId), ct);
            }, location => Path.Combine(ConfigLoader.GetDefaultDataDirectory(), "shell", location.Project.Id.Value)));
        services.AddSingleton(provider => new SessionExecutionEngine(provider.GetRequiredService<SessionStore>(), provider.GetRequiredService<ProviderResolver>(),
            provider.GetRequiredService<ToolRegistry>(), provider.GetRequiredService<ToolLocationFactory>(), provider.GetRequiredService<PermissionLocationMap>(),
            provider.GetRequiredService<SessionMovement>(), provider.GetRequiredService<SessionPromptPreparation>(), options.CodeModeLimits,
            provider.GetRequiredService<SessionSnapshotLocations>(), provider.GetRequiredService<SessionTitleService>(), options.Identity));
        services.AddSingleton<LoadReadInstructions>(provider => provider.GetRequiredService<SessionExecutionEngine>().LoadReadInstructionsAsync);
        services.AddSingleton(provider => new SessionSubagents(provider.GetRequiredService<IDatabase>(), provider.GetRequiredService<SessionStore>(),
            provider.GetRequiredService<SessionQueries>(), provider.GetRequiredService<SessionExecutionEngine>(), Stopping,
            provider.GetRequiredService<JobRuntime>()));
        services.AddSingleton(provider => new SessionBackgroundService(provider.GetRequiredService<SessionStore>(), provider.GetRequiredService<JobRuntime>(),
            provider.GetRequiredService<SessionExecutionEngine>(), Stopping));
        services.AddSingleton(provider => new ShellToolJobs(provider.GetRequiredService<JobRuntime>(), provider.GetRequiredService<SessionStore>(),
            provider.GetRequiredService<SessionExecutionEngine>(), Stopping));
        services.AddSingleton<SessionExecutionService>();
        services.AddHostedService<SessionExecutionService>(provider => provider.GetRequiredService<SessionExecutionService>());
        services.AddSingleton<CommandHostService>();
        services.AddSingleton<IPermissionAwareCommandShell, PermissionAwareCommandShell>();
        _services = host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        try
        {
            _hosted = _services.GetServices<IHostedService>().ToArray();
            // These public service starts attach pumps; no WebApplication/daemon/election or recovery is created.
            foreach (var service in _hosted) service.StartAsync(default).GetAwaiter().GetResult();
            _lifetime.Start();
        }
        catch
        {
            _lifetime.StopApplication();
            try { _services.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { _lifetime.Dispose(); }
            throw;
        }
    }

    internal Operation Enter(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing is not null, this);
            var operation = new Operation(this, ct);
            _operations.Add(operation);
            return operation;
        }
    }

    internal void RequireOpen()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_closing is not null, this);
    }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var operation = Enter(ct);
        return await action(operation.Token);
    }

    internal async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var operation = Enter(ct);
        await action(operation.Token);
    }

    internal async Task InitializeAsync(CancellationToken ct)
    {
        using var operation = Enter(ct);
        operation.Token.ThrowIfCancellationRequested();
        await using var connection = Get<IDatabase>().CreateConnection(); // Existing strict bootstrap/migration gate.
    }

    internal async Task WakeAsync(SessionId id, CancellationToken lifetime)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Wake requires a cancellable host lifetime.", nameof(lifetime));
        var operation = Enter(lifetime);
        try { await Get<SessionExecutionEngine>().WakeAsync(id, operation.Token); }
        catch { operation.Dispose(); throw; }
        _ = SettleAsync();
        async Task SettleAsync()
        {
            // Joining the owned drain must outlive cancellation of the wake request.
            try { await Get<SessionExecutionEngine>().AwaitOwnedDrainsAsync(CancellationToken.None); }
            finally { operation.Dispose(); }
        }
    }

    internal async IAsyncEnumerable<OpenCodeEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var feed = Get<IEventFeedService>();
        var subscriber = feed.Subscribe();
        try
        {
            await foreach (var frame in subscriber.ReadAllAsync(ct))
                yield return JsonSerializer.Deserialize(frame[6..].TrimEnd('\r', '\n'), OpenCodeJsonContext.Default.OpenCodeEvent)
                    ?? throw new JsonException("Missing event envelope.");
        }
        finally { feed.Unsubscribe(subscriber); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_closing ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        await Task.Yield();
        var failures = new List<Exception>();
        try { _lifetime.StopApplication(); }
        catch (Exception error) { failures.Add(error); }
        Task[] pending;
        lock (_gate) pending = _operations.Select(operation => operation.Done.Task).ToArray();
        await Task.WhenAll(pending);
        // Execution/jobs/title settle while Location permissions, forms, MCP and stores remain alive.
        await FinishAsync(() => Get<JobRuntime>().DisposeAsync().AsTask());
        await FinishAsync(() => Get<SessionSubagents>().DisposeAsync().AsTask());
        await FinishAsync(() => Get<SessionTitleService>().DisposeAsync().AsTask());
        await FinishAsync(() => Get<SessionSkillService>().DisposeAsync().AsTask());
        await FinishAsync(() => Get<SessionExecutionEngine>().AwaitOwnedDrainsAsync(CancellationToken.None));
        await FinishAsync(() => Get<SessionShellHostService>().StopAsync(default));
        await FinishAsync(() => Get<SessionExecutionService>().StopAsync(default));
        foreach (var service in _hosted.Reverse().Where(service => service is not SessionExecutionService))
            await FinishAsync(() => service.StopAsync(default));
        try { _lifetime.Complete(); }
        catch (Exception error) { failures.Add(error); }
        try { await _services.DisposeAsync(); }
        catch (Exception error) { failures.Add(error); }
        finally { _lifetime.Dispose(); }
        if (failures.Count > 0) throw new AggregateException("SDK host shutdown failed.", failures);

        async Task FinishAsync(Func<Task> finish)
        {
            try { await finish(); }
            catch (Exception error) { failures.Add(error); }
        }
    }

    private static LocalToolOptions Tools(LocationInfo location, IServiceProvider services, SdkHostOptions options)
    {
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } configured ? configured : Path.Combine(home, ".cache");
        if (!Path.IsPathFullyQualified(cache)) throw new NotSupportedException("XDG_CACHE_HOME must be absolute.");
        var bin = options.FormatterBin ?? Path.Combine(cache, "opencode", "bin");
        var forms = services.GetRequiredService<FormLocationServices>().ForLocation(location);
        return new LocalToolOptions(home, Ripgrep(options.RipgrepExecutable, bin), services.GetRequiredService<LoadReadInstructions>(),
            Formatter: new LocalFormatter(location.Directory, location.Project.Directory, bin, options.Clock), FormatterBin: bin,
            Hooks: options.ToolHooks?.Invoke(location), WebFetch: services.GetRequiredService<WebFetchTransport>(),
            ShellEnvironment: services.GetRequiredService<SessionEnvironment>().Get,
            McpForms: forms, QuestionForms: forms,
            McpCreated: (info, runtime) => services.GetRequiredService<IntegrationHostService>().AttachMcp(info, runtime,
                new McpEventBridge(info, runtime, services.GetRequiredService<IEventFeedService>())),
            McpOAuth: services.GetRequiredService<IntegrationHostService>().OAuth,
            Subagents: () => services.GetRequiredService<SessionSubagents>(),
            ShellRuntime: () => services.GetRequiredService<ShellLocationServices>().ForLocation(location),
            ShellJobs: () => services.GetRequiredService<ShellToolJobs>());
    }

    private static string Ripgrep(string? supplied, string bin)
    {
        var configured = supplied ?? Environment.GetEnvironmentVariable("OPENCODE_DOTNET_RIPGREP");
        if (configured is not null)
        {
            if (!Path.IsPathFullyQualified(configured) || !Executable(configured)) throw new NotSupportedException("SDK ripgrep must be an existing absolute executable; no fallback/download was attempted.");
            return Path.GetFullPath(configured);
        }
        var name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Append(bin))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
            if (Executable(candidate)) return candidate;
        }
        throw new NotSupportedException("Ripgrep is unavailable. Supply SdkHostOptions.RipgrepExecutable or OPENCODE_DOTNET_RIPGREP; no shell substitution or download is used.");
    }

    private static bool Executable(string path) => File.Exists(path) && (OperatingSystem.IsWindows()
        ? Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        : (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != UnixFileMode.None);
}
