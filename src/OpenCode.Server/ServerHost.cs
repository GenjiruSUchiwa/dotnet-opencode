namespace OpenCode.Server;

using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Formatting;
using OpenCode.Core.Locations;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Transfer;
using OpenCode.Core.Session.Subagents;
using OpenCode.Core.Pty;
using OpenCode.Core.Tools;
using OpenCode.Core.Permissions;
using OpenCode.Core.Snapshot;
using OpenCode.Core.Projects;
using OpenCode.Core.Shell;
using OpenCode.Core.Worktrees;
using OpenCode.Core.Jobs;
using OpenCode.Core.Shell.Jobs;
using OpenCode.Core.Session.Archive;
using OpenCode.Core.Session.Statistics;
using OpenCode.Core.Session.Skills;
using OpenCode.Core.Event;
using OpenCode.Schema;
using OpenCode.Protocol;
using OpenCode.Server.Endpoints;
using OpenCode.Server.Services;
using OpenCode.Server.Pty;
using OpenCode.Server.Integrations;
using OpenCode.Server.Shell;
using OpenCode.Server.Http;
using OpenCode.Server.Documentation;
using OpenCode.Server.Hosting;

public static class ServerHost
{
    public const int DefaultPort = OpenCodeChannel.ServicePort;

    public static string GetRegistrationPath(string? customPath = null)
    {
        var homeState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } stateRoot ? stateRoot : homeState;
        var path = Path.GetFullPath(customPath ?? Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVICE_FILE")
            ?? Path.Combine(state, "opencode", OpenCodeChannel.ServiceFileName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { state, homeState }.Any(root => path.StartsWith(
                Path.GetFullPath(Path.Combine(root, "opencode")) + Path.DirectorySeparatorChar, comparison))
            && !string.Equals(Path.GetFileName(path), OpenCodeChannel.ServiceFileName, comparison))
            throw new ArgumentException("Use service-dotnet.json, not another channel's registration, inside the OpenCode state directory.", nameof(customPath));
        return path;
    }

    public static WebApplication CreateApp(string[] args, int port = DefaultPort, string? registrationFile = null, PersistentPtyOptions? persistentPty = null, TimeProvider? clock = null)
        => CreateApp(args, StartupDiagnostics.Begin(args), port, registrationFile, persistentPty, clock);

    public static WebApplication CreateStandaloneApp(string password, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var diagnostics = StartupDiagnostics.InMemory();
        try { return CreateAppCore([], 0, null, diagnostics, null, password, clock); }
        catch (Exception error) { diagnostics.Fail(error); throw; }
    }

    internal static WebApplication CreateApp(string[] args, StartupDiagnostics diagnostics, int port = DefaultPort, string? registrationFile = null, PersistentPtyOptions? persistentPty = null, TimeProvider? clock = null)
    {
        try { return CreateAppCore(args, port, registrationFile, diagnostics, persistentPty, clock: clock); }
        catch (Exception error) { diagnostics.Fail(error); throw; }
    }

    [SuppressMessage("Design", "MA0015", Justification = "Server startup diagnostics retain their established validation messages and selected-port field name; schema composition must not change public failure text.")]
    private static WebApplication CreateAppCore(string[] args, int port, string? registrationFile, StartupDiagnostics diagnostics, PersistentPtyOptions? persistentPty,
        string? standalonePassword = null, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        diagnostics.Phase("configuration");
        // The managed client passes a one-use handoff only to the new daemon's
        // environment, not argv or persistent config. Remove it before any tool
        // or terminal child can inherit that credential.
        var ptyHandoff = standalonePassword is null ? Environment.GetEnvironmentVariable("OPENCODE_DOTNET_PTY_HANDOFF") : null;
        if (standalonePassword is null) Environment.SetEnvironmentVariable("OPENCODE_DOTNET_PTY_HANDOFF", null);
        if (persistentPty is null && ptyHandoff is not null)
        {
            var inherited = JsonSerializer.Deserialize(ptyHandoff, OpenCodeJsonContext.Default.PersistentPtyHandoff)
                ?? throw new InvalidDataException("The persistent terminal handoff is invalid.");
            persistentPty = new PersistentPtyOptions(inherited.Directory,
                Environment.GetEnvironmentVariable("OPENCODE_DOTNET_PTY_BIN") ?? Environment.GetEnvironmentVariable("OPENCODE_PTY_BIN"), inherited);
        }
        string? configFile = null;
        int? argumentPort = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "serve" or "--service") continue;
            var name = args[index];
            if (name is not ("--port" or "-p" or "--registration-file" or "--service-config" or "--startup-id" or "--startup-report") || index + 1 >= args.Length)
                throw new ArgumentException($"Unknown or incomplete server option: {name}");
            var value = args[++index];
            switch (name)
            {
                case "--port" or "-p":
                    argumentPort = ParsePort(value);
                    break;
                case "--registration-file":
                    registrationFile = value;
                    break;
                case "--service-config":
                    configFile = value;
                    break;
            }
        }

        configFile = standalonePassword is not null ? "" : Path.GetFullPath(configFile ?? Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVICE_CONFIG")
            ?? Path.Combine(ConfigLoader.GetDefaultConfigDirectory(), OpenCodeChannel.ServiceFileName));
        if (standalonePassword is null && Path.GetFileName(configFile) != OpenCodeChannel.ServiceFileName)
            throw new ArgumentException("The channel service config must be named service-dotnet.json.");
        var config = standalonePassword is null && File.Exists(configFile)
            ? JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject
                ?? throw new InvalidDataException("The .NET service config must be a JSON object.")
            : new JsonObject();
        if (config["hostname"] is JsonNode hostname && hostname.GetValue<string>() != "127.0.0.1")
            throw new InvalidDataException("The .NET local service supports only hostname 127.0.0.1.");
        var environmentPort = Environment.GetEnvironmentVariable("OPENCODE_DOTNET_PORT");
        var selectedPort = standalonePassword is not null ? 0 : argumentPort ?? (port != DefaultPort ? port : environmentPort is not null
            ? ParsePort(environmentPort) : config["port"]?.GetValue<int>() ?? port);
        ArgumentOutOfRangeException.ThrowIfLessThan(selectedPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(selectedPort, 65535);
        var file = standalonePassword is null ? GetRegistrationPath(registrationFile) : "";
        if (standalonePassword is null && string.Equals(file, configFile, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Registration and service config must be different files.");
        var managed = standalonePassword is null ? new ServiceLifetime(file, configFile, config, diagnostics, time) : null;
        var standalone = standalonePassword is not null ? new StandaloneLifetime(standalonePassword, diagnostics) : null;
        IServerIdentity service = managed is not null ? managed : standalone!;

        // Service flags are parsed above rather than passed to ASP.NET's unrelated configuration parser.
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.Services.AddSingleton(time);
        if (standalone is not null)
        {
            builder.Services.AddSingleton<IHostLifetime, StandaloneHostLifetime>();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            // Hosting request summaries include the query string, which can carry
            // the supported auth_token/PTY ticket. Do not log those private URLs.
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        }
        builder.Services.AddNativeRequestValidation();
        builder.Logging.AddProvider(diagnostics);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, selectedPort));
        builder.Services.AddSingleton<IServerIdentity>(service);
        if (managed is not null)
        {
            builder.Services.AddSingleton(managed);
            builder.Services.AddHostedService<ServiceLifetime>(services => services.GetRequiredService<ServiceLifetime>());
        }
        if (standalone is not null)
        {
            builder.Services.AddSingleton(standalone);
            builder.Services.AddHostedService<StandaloneLifetime>(services => services.GetRequiredService<StandaloneLifetime>());
        }
        builder.Services.AddSingleton<HttpClient>();
        // Only the connection/schema owner is a singleton. Core creates an EF
        // context per operation, enlisting in the owner's native transaction.
        // Do not register a shared DbContext or run EF schema initialization here.
        builder.Services.AddSingleton<IDatabase>(_ => new SqliteDatabase(clock: time));
        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<IJobBackgroundStore, JobBackgroundStore>();
        builder.Services.AddSingleton(services => new JobRuntime(services.GetRequiredService<IJobBackgroundStore>(),
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, time));
        builder.Services.AddSingleton<SessionEnvironment>();
        builder.Services.AddSingleton<SessionMutations>();
        builder.Services.AddSingleton<SessionInstructionEntries>();
        builder.Services.AddSingleton<ISessionShellLifecycle, SessionShellLifecycle>();
        builder.Services.AddSingleton<SessionQueries>();
        builder.Services.AddSingleton<ISessionArchivePersistence, SessionArchivePersistence>();
        builder.Services.AddSingleton<SessionArchiveService>();
        builder.Services.AddSingleton<SessionStatistics>();
        builder.Services.AddSingleton<ISessionSkillPublisher, SessionSkillPublisher>();
        builder.Services.AddSessionSkillServices();
        builder.Services.AddSingleton<ProjectQueries>();
        builder.Services.AddSingleton<WorktreeService>();
        builder.Services.AddSingleton(services => new ProjectMutations(services.GetRequiredService<IDatabase>(),
            services.GetRequiredService<IEventFeedService>().Publish));
        builder.Services.AddSingleton<SessionSnapshotLocations>();
        builder.Services.AddSingleton<SessionRevertOperations>();
        builder.Services.AddSingleton(services => new SessionTitleService(services.GetRequiredService<IDatabase>(),
            services.GetRequiredService<SessionStore>(), services.GetRequiredService<ProviderResolver>(),
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        builder.Services.AddSingleton(services => new SessionPromptPreparation(services.GetRequiredService<SessionStore>(),
            commitRevert: (session, ct) => SessionRevertOperations.CommitPreparedAsync(services.GetRequiredService<IDatabase>(), session, ct)));
        builder.Services.AddSingleton<ProviderResolver>();
        builder.Services.AddSingleton<ToolRegistry>();
        builder.Services.AddSingleton<SqlitePermissionGrantStore>();
        builder.Services.AddSingleton<IPermissionGrantStore>(services => services.GetRequiredService<SqlitePermissionGrantStore>());
        builder.Services.AddSingleton<IPermissionSavedStore>(services => services.GetRequiredService<SqlitePermissionGrantStore>());
        IServiceProvider? toolServices = null;
        builder.Services.AddLocalToolLocations(location => LocalToolOptionsFor(location,
            toolServices ?? throw new InvalidOperationException("The host service provider is not available yet.")),
            locationClosed: async location =>
            {
                var services = toolServices ?? throw new InvalidOperationException("The host service provider is not available yet.");
                services.GetRequiredService<FormLocationServices>().Invalidate(new(location.Directory, location.WorkspaceId));
                await services.GetRequiredService<IntegrationHostService>().InvalidateAsync(new(location.Directory, location.WorkspaceId));
                await services.GetRequiredService<ShellLocationServices>().InvalidateAsync(new(location.Directory, location.WorkspaceId));
                await services.GetRequiredService<PtyLocationMap>().InvalidateAsync(new(location.Directory, location.WorkspaceId));
            });
        builder.Services.AddSingleton<FormLocationServices>();
        builder.Services.AddHostedService<FormLocationServices>(services => services.GetRequiredService<FormLocationServices>());
        builder.Services.AddIntegrationServices();
        builder.Services.AddNativeWebSearch();
        builder.Services.AddPersistentPty(persistentPty);
        builder.Services.AddShellServices(new ShellHostOptions(
            ResolveShell: (location, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(PtyShellSelection.Resolve(location)); },
            FlushPlugins: (location, ct) => FlushNativeLocationAsync(location,
                toolServices ?? throw new InvalidOperationException("The host service provider is not available yet."), ct).AsTask(),
            OutputDirectory: location => Path.Combine(ConfigLoader.GetDefaultDataDirectory(), "shell", location.Project.Id.Value)));
        builder.Services.AddSingleton<SessionMovement>();
        builder.Services.AddSingleton<SessionTransfer>();
        builder.Services.AddSingleton(provider => new SessionExecutionEngine(
            provider.GetRequiredService<SessionStore>(), provider.GetRequiredService<ProviderResolver>(),
            provider.GetRequiredService<ToolRegistry>(), provider.GetRequiredService<ToolLocationFactory>(),
            provider.GetRequiredService<PermissionLocationMap>(), provider.GetRequiredService<SessionMovement>(),
            prompts: provider.GetRequiredService<SessionPromptPreparation>(), snapshots: provider.GetRequiredService<SessionSnapshotLocations>(),
            titles: provider.GetRequiredService<SessionTitleService>()));
        builder.Services.AddSingleton<LoadReadInstructions>(provider =>
            provider.GetRequiredService<SessionExecutionEngine>().LoadReadInstructionsAsync);
        builder.Services.AddSingleton<SessionExecutionService>();
        builder.Services.AddSingleton(services => new ShellToolJobs(services.GetRequiredService<JobRuntime>(),
            services.GetRequiredService<SessionStore>(), services.GetRequiredService<SessionExecutionEngine>(),
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        builder.Services.AddSingleton<IShellToolJobs>(services => services.GetRequiredService<ShellToolJobs>());
        builder.Services.AddSingleton(services => new SessionSubagents(services.GetRequiredService<IDatabase>(),
            services.GetRequiredService<SessionStore>(), services.GetRequiredService<SessionQueries>(),
            services.GetRequiredService<SessionExecutionEngine>(), services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
            services.GetRequiredService<JobRuntime>()));
        builder.Services.AddSingleton(services => new SessionBackgroundService(services.GetRequiredService<SessionStore>(),
            services.GetRequiredService<JobRuntime>(), services.GetRequiredService<SessionExecutionEngine>(),
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        builder.Services.AddHostedService<SessionSubagentsLifetime>();
        builder.Services.AddSingleton<CommandHostService>();
        builder.Services.AddSingleton<IPermissionAwareCommandShell, PermissionAwareCommandShell>();
        builder.Services.AddHostedService<SessionExecutionService>(services => services.GetRequiredService<SessionExecutionService>());
        builder.Services.AddSingleton<IEventFeedService, EventFeedService>();
        builder.Services.AddLocalPty(new PtyHostOptions(
            ResolveLocation: (directory, workspace, ct) => new(ProjectDiscovery.ResolveAsync(
                (toolServices ?? throw new InvalidOperationException("The host service provider is not available yet."))
                    .GetRequiredService<IDatabase>(), directory, workspace, ct)),
            ResolveShell: PtyShellSelection.Resolve,
            Authorized: service.Authorized,
            FlushPlugins: (location, ct) => FlushNativeLocationAsync(location,
                toolServices ?? throw new InvalidOperationException("The host service provider is not available yet."), ct),
            Environment: (_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                // Upstream's default PTY environment injection is empty; no shell integration is installed.
                return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
            },
            Cors: config["cors"]?.Deserialize<string[]>()));

        diagnostics.Phase("host-build");
        var app = builder.Build();
        toolServices = app.Services;
        if (managed is not null) managed.Application = app;
        if (standalone is not null) standalone.Application = app;
        // The source applies the same origin policy to every HTTP route, not only PTY.
        app.UseCors("opencode-pty");
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!service.Authorized(context.Request)
                && !PtyRequestPolicy.HasPtyConnectTicketURL(context.Request)
                && !PtyRequestPolicy.HasPersistentPtyConnectTicketURL(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"Secure Area\"";
                await Results.Json(new { _tag = "UnauthorizedError", message = "Authentication required" }, statusCode: 401).ExecuteAsync(context);
                return;
            }
            var state = service.State;
            if (state != "ready"
                && !(HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/api/health")
                && !(HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/api/service/stop"))
            {
                if (state != "failed") context.Response.Headers.RetryAfter = "1";
                await Results.Json(new
                {
                    code = "service_" + state,
                    message = state == "failed" ? "The background service could not initialize. Inspect diagnostics and explicitly stop the verified instance before restarting." : "The service is not ready."
                }, statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
                return;
            }
            await next(context);
        });
        app.UseNativeRequestValidation();
        if (managed is not null) app.MapPost("/api/service/stop", (HttpContext context) =>
        {
            if (context.Request.Headers["X-OpenCode-Service-ID"] != service.Id)
                return Results.Conflict(new { message = "The service instance has changed." });
            context.Response.OnCompleted(() =>
            {
                app.Lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted();
        });
        app.MapHealthEndpoints();
        app.MapEventEndpoints();
        app.MapSessionEndpoints();
        app.MapSessionArchiveExportEndpoint();
        app.MapSessionArchiveImportEndpoint();
        app.MapSessionStatsEndpoints();
        app.MapSessionSkillEndpoints();
        app.MapProviderEndpoints();
        app.MapLocationEndpoints();
        app.MapConfigEndpoints();
        app.MapAgentEndpoints();
        app.MapModelEndpoints();
        app.MapPermissionEndpoints();
        app.MapPtyEndpoints();
        app.MapPersistentPtyEndpoints();
        app.MapFeatureEndpoints();
        app.MapSkillEndpoints();
        app.MapMcpEndpoints();
        app.MapFormEndpoints();
        app.MapIntegrationEndpoints();
        app.MapWebSearchEndpoints();
        app.MapCredentialEndpoints();
        app.MapShellEndpoints();
        app.MapVcsEndpoints();
        app.MapProjectEndpoints();
        app.MapWorktreeEndpoints();
        app.MapNativeOpenApi();
        // Materialize bindings before listener registration/ready publication. A
        // malformed endpoint must not turn every request, including health, into 500.
        diagnostics.Phase("routes");
        foreach (var source in ((IEndpointRouteBuilder)app).DataSources) _ = source.Endpoints;
        return app;
    }

    [SuppressMessage("Design", "MA0015", Justification = "Keep the existing service-port validation message without adding a new parameter suffix.")]
    private static int ParsePort(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var port) && port is >= 1 and <= 65535
        ? port : throw new ArgumentException("The service port must be between 1 and 65535.");

    private static LocalToolOptions LocalToolOptionsFor(LocationInfo location, IServiceProvider services)
    {
        // Do not pass a no-op/throwing delegate: post-read discovery catches loader
        // failures. Missing composition must fail before any tool is constructed.
        var load = services.GetService<LoadReadInstructions>()
            ?? throw new NotSupportedException("The session-owned LoadReadInstructions implementation must be registered before local tool Locations can load.");
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } root
            ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        if (!Path.IsPathFullyQualified(cache)) throw new NotSupportedException("XDG_CACHE_HOME must be absolute for local tool composition.");
        // Upstream Global.bin is a lookup fallback, not a directory to create or an installer.
        var bin = Path.Combine(cache, "opencode", "bin");
        return new LocalToolOptions(Home: home, RipgrepExecutable: ResolveRipgrep(bin), LoadReadInstructions: load,
            Formatter: new LocalFormatter(location.Directory, location.Project.Directory, bin, services.GetRequiredService<TimeProvider>()), FormatterBin: bin,
            ShellEnvironment: services.GetRequiredService<SessionEnvironment>().Get,
            McpForms: services.GetRequiredService<FormLocationServices>().ForLocation(location),
            McpCreated: (info, runtime) => services.GetRequiredService<IntegrationHostService>().AttachMcp(info, runtime,
                new McpEventBridge(info, runtime, services.GetRequiredService<IEventFeedService>())),
            McpOAuth: services.GetRequiredService<IntegrationHostService>().OAuth,
            QuestionForms: services.GetRequiredService<FormLocationServices>().ForLocation(location),
            Subagents: () => services.GetRequiredService<SessionSubagents>(),
            ShellRuntime: () => services.GetRequiredService<ShellLocationServices>().ForLocation(location),
            ShellJobs: () => services.GetRequiredService<IShellToolJobs>(),
            Plugins: OpenCode.Server.Plugins.NativePluginComposition.Definitions(services, location),
            PluginChanged: id => OpenCode.Server.Plugins.NativePluginComposition.Publish(services, location, id),
            WebSearchReady: ct => services.GetRequiredService<WebSearchPluginSource>().ReadyAsync(location, ct));
    }

    internal static async ValueTask FlushNativeLocationAsync(LocationInfo location, IServiceProvider services, CancellationToken ct)
    {
        // Supported native counterpart of PluginSupervisor.flush: this acquisition
        // validates configured/discovered plugin sources, loads the real command
        // snapshot, settles shared MCP discovery, and awaits its registry reload.
        // It does not claim unsupported third-party plugin generations are ready.
        await using var snapshot = await services.GetRequiredService<CommandHostService>()
            .AcquireAsync(new(location.Directory, location.WorkspaceId), ct);
    }

    private static string ResolveRipgrep(string bin)
    {
        if (Environment.GetEnvironmentVariable("OPENCODE_DOTNET_RIPGREP") is { Length: > 0 } configured)
        {
            if (!Path.IsPathFullyQualified(configured) || !ExecutableFile(configured))
                throw new NotSupportedException("OPENCODE_DOTNET_RIPGREP must identify an existing absolute executable file. No fallback or download was attempted.");
            return Path.GetFullPath(configured);
        }
        var name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Append(bin))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
            if (ExecutableFile(candidate)) return candidate;
        }
        throw new NotSupportedException("Ripgrep is unavailable on PATH and in the existing OpenCode binary cache. Supply OPENCODE_DOTNET_RIPGREP as an absolute executable path; this host does not download or substitute a shell.");
    }

    private static bool ExecutableFile(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != UnixFileMode.None;
    }
}

public sealed class ServiceAlreadyRunningException(string message, Exception inner) : IOException(message, inner);

internal sealed class ServiceLifetime : IHostedLifecycleService, IDisposable, IServerIdentity
{
    private readonly string _file;
    private readonly string _configFile;
    private readonly JsonObject _config;
    private readonly StartupDiagnostics _diagnostics;
    private readonly TimeProvider _clock;
    private readonly string _password;
    private readonly ServerCredentials _credentials;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private FileStream? _lease;
    private Task _monitor = Task.CompletedTask;
    private Task _boot = Task.CompletedTask;
    private string _state = "starting";
    private bool _published;
    private int _disposed;

    internal WebApplication Application { private get; set; } = null!;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string? Url { get; private set; }
    internal string State => Volatile.Read(ref _state);
    internal bool Ready => State == "ready";
    string IServerIdentity.Id => Id;
    string? IServerIdentity.Url => Url;
    string IServerIdentity.State => State;
    bool IServerIdentity.Authorized(HttpRequest request) => Authorized(request);

    internal ServiceLifetime(string file, string configFile, JsonObject config, StartupDiagnostics diagnostics, TimeProvider clock)
    {
        _clock = clock;
        _file = file;
        _configFile = configFile;
        _config = config;
        _diagnostics = diagnostics;
        _password = config["password"]?.GetValue<string>() is { Length: > 0 } password
            ? password : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _credentials = new ServerCredentials(_password);
    }

    internal bool Authorized(string header)
        => _credentials.Authorized(header);

    internal bool Authorized(HttpRequest request)
    {
        // Browser EventSource/WebSocket clients cannot supply Authorization.
        // Match source precedence: a nonempty auth_token overrides the header.
        return _credentials.Authorized(request);
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _diagnostics.Phase("election");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        try
        {
            // Never unlink this lock file: replacing its inode would allow two owners.
            _lease = new FileStream(_file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
        {
            _diagnostics.Complete("incumbent");
            throw new ServiceAlreadyRunningException("Another .NET service owns this channel registration.", error);
        }
        // Older instances may not hold this election lock. A responding registered
        // endpoint must not be overwritten merely because a contender acquired it.
        if (!File.Exists(_file)) return;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_file, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("The existing .NET registration is invalid. Verify it before starting another instance; automatic replacement is unsupported.");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(2) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/health"));
        if (document.RootElement.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.String)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password.GetString()}")));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            throw new InvalidOperationException("The registered endpoint still responds. Verify and explicitly stop that instance before replacement; its registration was not overwritten.");
        }
        catch (HttpRequestException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The registered endpoint is unresponsive. Forced recovery is unsupported; verify the instance before attempting another start.");
        }
        // Connection refusal is not proof of process death: a previous owner may
        // still be settling claims after closing its listener. PID reuse blocks
        // conservatively rather than allowing two recovery owners.
        if (!document.RootElement.TryGetProperty("pid", out var previousPid) || !previousPid.TryGetInt32(out var pid) || pid <= 0)
            throw new InvalidOperationException("The previous registered process identity is unavailable; automatic claim recovery is unsafe.");
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            if (!previous.HasExited)
                throw new InvalidOperationException("The previous registered process is still alive. Wait for its shutdown before restarting; no claim recovery was attempted.");
        }
        catch (ArgumentException) { } // The recorded process no longer exists.
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _diagnostics.Phase("listen");
        return Task.CompletedTask;
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        _diagnostics.Phase("registration");
        var addresses = Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        Url = addresses?.SingleOrDefault() ?? throw new InvalidOperationException("The local service has no unique listening address.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1")
            throw new InvalidOperationException("The local service must listen only on IPv4 loopback.");

        // Keep the private credential across restarts, but never inherit it through tool environments.
        _config["password"] = _password;
        await WritePrivateAsync(_configFile, _config.ToJsonString(), cancellationToken);
        await WritePrivateAsync(_file, JsonSerializer.Serialize(new
        {
            id = Id,
            version = OpenCodeChannel.ServiceVersion,
            buildID = ApplicationBuild.Id,
            url = Url,
            pid = Environment.ProcessId,
            password = _password,
            startupID = _diagnostics.Nonce
        }), cancellationToken);
        _published = true;
        _diagnostics.Phase("storage");
        _monitor = MonitorAsync(_monitorCancellation.Token);
        // Like upstream process.ts, expose authenticated starting/failed health
        // after binding, but keep application routes unavailable until boot completes.
        _boot = Task.Run(async () =>
        {
            if (_monitorCancellation.IsCancellationRequested) return;
            try
            {
                await using var connection = Application.Services.GetRequiredService<IDatabase>().CreateConnection();
                _diagnostics.Phase("session-recovery");
                await Application.Services.GetRequiredService<SessionExecutionService>().StartRecovery();
                if (_monitorCancellation.IsCancellationRequested) return;
                _diagnostics.Complete("ready");
                Interlocked.CompareExchange(ref _state, "ready", "starting");
            }
            catch (Exception error)
            {
                _diagnostics.Fail(error);
                Interlocked.CompareExchange(ref _state, "failed", "starting");
                Application.Logger.LogError(error, "The .NET service could not initialize; health remains failed.");
            }
        }, CancellationToken.None);
    }

    private async Task WritePrivateAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Id + ".tmp";
        try
        {
            await using (var stream = CreatePrivateFile(temp))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private bool OwnsRegistration()
    {
        if (!_published) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_file));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == Id
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == OpenCodeChannel.ServiceVersion
                && root.TryGetProperty("buildID", out var build) && build.ValueKind == JsonValueKind.String && build.GetString() == ApplicationBuild.Id
                && root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() == Url
                && root.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var process) && process == Environment.ProcessId
                && root.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.String && password.GetString() == _password;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (OwnsRegistration()) continue;
                Interlocked.Exchange(ref _state, "stopping");
                Application.Logger.LogWarning("The .NET service registration was removed or replaced; shutting down this instance.");
                Application.Lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    [SuppressMessage("Usage", "MA0042", Justification = "The lifecycle contract cancels monitor callbacks synchronously before returning the completed stopping task; CancelAsync would move those callbacks to another execution context.")]
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _state, "stopping");
        _monitorCancellation.Cancel();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.WhenAll(_monitor, _boot);

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Cleanup();
        return Task.CompletedTask;
    }

    private void Cleanup()
    {
        try
        {
            if (_lease is not null && OwnsRegistration()) File.Delete(_file);
        }
        catch (IOException) { Application.Logger.LogWarning("Could not remove the .NET service registration."); }
        catch (UnauthorizedAccessException) { Application.Logger.LogWarning("Could not remove the .NET service registration."); }
        _published = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _state, "stopping");
        _monitorCancellation.Cancel();
        try { Task.WhenAll(_monitor, _boot).GetAwaiter().GetResult(); }
        finally
        {
            try { Cleanup(); }
            finally
            {
                _lease?.Dispose();
                _monitorCancellation.Dispose();
            }
        }
    }
}
