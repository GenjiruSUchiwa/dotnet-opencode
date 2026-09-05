namespace OpenCode.Client;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;
using OpenCode.Protocol;

public sealed record ServiceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("password")] string? Password = null,
    [property: JsonPropertyName("startupID")] string? StartupId = null,
    [property: JsonPropertyName("buildID")] string? BuildId = null,
    [property: JsonPropertyName("buildTimestamp")] long? BuildTimestamp = null
);

public sealed record ServiceEndpoint(string Url, string? Password = null)
{
    public void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Password))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{Password}")));
    }
}

/// <summary>Discovery and lifecycle for the separately registered .NET service.</summary>
public static class ServiceDaemon
{
    public const int DefaultPort = OpenCodeChannel.ServicePort;
    public const string DefaultVersion = ApplicationBuild.Version;
    public const string Application = OpenCodeChannel.Application;

    public static string GetDefaultRegistrationFile() => GetRegistrationFile(null);

    private static string GetRegistrationFile(string? file)
    {
        var homeState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } stateRoot ? stateRoot : homeState;
        var path = Path.GetFullPath(file ?? Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVICE_FILE")
            ?? Path.Combine(state, "opencode", OpenCodeChannel.ServiceFileName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { state, homeState }.Any(root => path.StartsWith(
                Path.GetFullPath(Path.Combine(root, "opencode")) + Path.DirectorySeparatorChar, comparison))
            && !string.Equals(Path.GetFileName(path), OpenCodeChannel.ServiceFileName, comparison))
            throw new ArgumentException($"Use {OpenCodeChannel.ServiceFileName}, not another channel's registration, inside the OpenCode state directory.", nameof(file));
        return path;
    }

    public static async Task<ServiceEndpoint?> DiscoverAsync(string? registrationFile = null, CancellationToken ct = default, TimeProvider? clock = null)
    {
        return await DiscoverWithOptionsAsync(new ServiceDiscoveryOptions { RegistrationFile = registrationFile, Clock = clock ?? TimeProvider.System }, ct).ConfigureAwait(false);
    }

    public static async Task<ServiceEndpoint?> DiscoverWithOptionsAsync(ServiceDiscoveryOptions options, CancellationToken ct = default)
    {
        var service = await InspectAsync(options, ct).ConfigureAwait(false);
        return service is { State: ServiceState.Ready, Compatible: true } ? service.Endpoint : null;
    }

    /// <summary>Read-only identity/state inspection. Explicit servers never consult local registration.</summary>
    public static async Task<ServiceStatus?> InspectAsync(ServiceDiscoveryOptions options, CancellationToken ct = default)
    {
        var info = options.Server is null ? await ReadAsync(GetRegistrationFile(options.RegistrationFile), ct).ConfigureAwait(false) : null;
        var endpoint = options.Server ?? (info is null ? null : new ServiceEndpoint(info.Url, info.Password));
        if (endpoint is null) return null;
        var result = await ProbeAsync(endpoint, info, options, ct).ConfigureAwait(false);
        if (options.Server is not null && result.Error is not null) throw result.Error;
        return result.Status;
    }

    public static Task<ServiceEndpoint> EnsureAsync(
        string? registrationFile = null, int? port = null, CancellationToken ct = default, TimeProvider? clock = null) =>
        EnsureWithOptionsAsync(new ServiceStartOptions { RegistrationFile = registrationFile, Port = port, Clock = clock ?? TimeProvider.System }, ct);

    /// <summary>Starts a packaged server or an explicit command prefix, never a source-project build.</summary>
    public static async Task<ServiceEndpoint> EnsureWithOptionsAsync(ServiceStartOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options.Clock);
        if (options.Port is int port)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1, nameof(options));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535, nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StartupTimeout, TimeSpan.Zero);
        var file = options.Server is null ? GetRegistrationFile(options.RegistrationFile) : null;
        var expectedBuild = options.ExpectedBuildId ?? ApplicationBuild.Id;
        if (!IsBuildId(expectedBuild)) throw new ArgumentException("Expected build identity must be a lowercase SHA-256 fingerprint.", nameof(options));
        if (OpenCodeChannel.IsLocal && (expectedBuild != ApplicationBuild.Id || ApplicationBuild.Timestamp <= 0))
            throw new ServiceLifecycleException(ServiceFailure.InvalidConfiguration, "A dotnet-local client must use its own timestamped build identity.",
                "Use ./run.ps1; local build matching cannot be overridden.");
        var replaced = false;
        PersistentPtyHandoff? ptyHandoff = null;
        var started = options.Clock.GetTimestamp();
        var children = new List<ServiceContender>();
        var spawnDelay = ServiceTiming.SpawnDelay;
        TimeSpan? lastSpawn = null;
        ServiceInfo? timedOutInfo = null;
        var timeouts = 0;
        try
        {
            while (options.Clock.GetElapsedTime(started) < options.StartupTimeout)
            {
                ct.ThrowIfCancellationRequested();
                var info = file is null ? null : await ReadAsync(file, ct).ConfigureAwait(false);
                var endpoint = options.Server ?? (info is null ? null : new ServiceEndpoint(info.Url, info.Password));
                var result = endpoint is null ? new ProbeResult(null, null) : await ProbeAsync(endpoint, info, options, ct).ConfigureAwait(false);
                if (result.Error?.Code != ServiceFailure.ProbeTimeout || info is null)
                {
                    timedOutInfo = null;
                    timeouts = 0;
                }
                if (result.Status is { } service)
                {
                    if (!service.Compatible)
                    {
                        if (OpenCodeChannel.IsLocal && options.Server is null && service.Channel == OpenCodeChannel.Name
                            && service.State == ServiceState.Stopping && service.BuildTimestamp is > 0
                            && service.BuildTimestamp < ApplicationBuild.Timestamp)
                        {
                            await Task.Delay(ServiceTiming.PollInterval, options.Clock, ct).ConfigureAwait(false);
                            continue;
                        }
                        var mismatch = new ServiceLifecycleException(service.BuildId != expectedBuild && options.Server is null
                                ? ServiceFailure.IncompatibleBuild : ServiceFailure.IncompatibleVersion,
                            "The selected server does not match this application's required version/build identity.",
                            options.Server is null
                                ? OpenCodeChannel.IsLocal
                                    ? "Use the matching dotnet-local source build. Only strictly older verified local servers can be replaced; a mismatched server will not be used."
                                    : "Do not continue with stale code. Finish active work, then use ./run.ps1 stop for the verified .NET instance and rerun ./run.ps1 if automatic idle replacement is unavailable."
                                : "Choose a compatible explicit server or deliberately change its compatibility policy. Explicit servers are never replaced automatically.");
                        if (options.Server is not null || !options.ReplaceIncompatible || replaced || info is null
                            || service.Channel != OpenCodeChannel.Name
                            || (OpenCodeChannel.IsLocal ? service.State is not (ServiceState.Ready or ServiceState.Failed or ServiceState.Starting) : service.State != ServiceState.Ready)
                            || !(options.VersionPredicate?.Invoke(DefaultVersion) ?? (options.Version is null || options.Version == DefaultVersion)))
                            throw mismatch;
                        if (OpenCodeChannel.IsLocal && (service.BuildTimestamp is not > 0 || service.BuildTimestamp >= ApplicationBuild.Timestamp))
                            throw new ServiceLifecycleException(ServiceFailure.IncompatibleBuild,
                                "The dotnet-local server is newer than this client, or has a conflicting timestamp.",
                                "Rebuild the local client. A newer server is never downgraded and a mismatched UI is never connected.");
                        ServiceProcess.ValidateBuild(options.Command, expectedBuild);
                        // Local source upgrades retire the older host even during work;
                        // normal shutdown preserves the existing durable recovery claims.
                        if (!OpenCodeChannel.IsLocal && !await IsIdleAsync(info, ct).ConfigureAwait(false)) throw mismatch;
                        ptyHandoff = service.State == ServiceState.Ready ? await PreparePtyHandoffAsync(info, ct).ConfigureAwait(false) : null;
                        await StopRegisteredAsync(file!, info, ct, options.Clock,
                            OpenCodeChannel.IsLocal ? TimeSpan.FromSeconds(30) : null).ConfigureAwait(false);
                        replaced = true;
                        lastSpawn = null;
                        spawnDelay = ServiceTiming.SpawnDelay;
                        timeouts = 0;
                        timedOutInfo = null;
                        continue;
                    }
                    if (service.State == ServiceState.Ready) return service.Endpoint;
                    if (service.State == ServiceState.Failed)
                    {
                        if (info?.StartupId is string nonce && ServiceStartupDiagnostics.IsCanonicalNonce(nonce))
                            throw await ServiceStartupDiagnostics.FailureAsync(nonce, info.Pid, ServiceFailure.StartupFailed, ct).ConfigureAwait(false);
                        throw new ServiceLifecycleException(ServiceFailure.StartupFailed, "The authenticated server reports startup failure.",
                            "Inspect the server diagnostics and resolve the startup error. Explicitly stop the verified instance before restarting it.");
                    }
                    timedOutInfo = null;
                    timeouts = 0;
                    spawnDelay = ServiceTiming.SpawnDelay;
                }
                if (result.Error is { } error)
                {
                    if (options.Server is not null) throw error;
                    if (error.Code is ServiceFailure.Unauthorized or ServiceFailure.InvalidIdentity) throw error;
                    if (error.Code == ServiceFailure.ProbeTimeout && info is not null)
                    {
                        timeouts = info == timedOutInfo ? timeouts + 1 : 1;
                        timedOutInfo = info;
                        if (timeouts >= 3)
                            throw new ServiceLifecycleException(ServiceFailure.RecoveryUnsupported,
                                "The same registered service timed out three consecutive health probes.",
                                "Verify the .NET instance and inspect its diagnostics. Forced recovery is unsupported; no PID was signalled and registration was not deleted.");
                    }
                    else { timedOutInfo = null; timeouts = 0; }
                }

                ServiceLifecycleException? failure = null;
                var finished = children.Where(child => child.ExitStatus is not null).ToArray();
                if (finished.Any(child => child.ExitStatus is { ExitCode: 0, Canceled: false, Signal: null }))
                    spawnDelay = TimeSpan.FromTicks(Math.Min(spawnDelay.Ticks * 2, ServiceTiming.MaxSpawnDelay.Ticks));
                foreach (var child in finished)
                {
                    if (child.ExitStatus is not { ExitCode: 0, Canceled: false, Signal: null })
                        failure ??= await ServiceStartupDiagnostics.FailureAsync(child.Nonce, child.Process.ProcessId, ServiceFailure.ContenderFailed, ct).ConfigureAwait(false);
                    children.Remove(child);
                    child.Dispose();
                }
                if (result.Status is null && options.Server is null)
                {
                    if (failure is not null && children.Count == 0) throw failure;
                    if (lastSpawn is null && info is not null) lastSpawn = options.Clock.GetElapsedTime(started);
                    // One candidate plus one lock probe; slow candidates are never killed.
                    if (children.Count < 2 && (lastSpawn is null || options.Clock.GetElapsedTime(started) - lastSpawn.Value >= spawnDelay))
                    {
                        try { children.Add(ServiceProcess.Start(options.Command, file!, options.Port, expectedBuild, ptyHandoff)); }
                        catch (Exception cause) when (cause is not ServiceLifecycleException &&
                            cause is (System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException or NotSupportedException or JsonException or UnauthorizedAccessException))
                        {
                            throw new ServiceLifecycleException(ServiceFailure.ContenderFailed, "The .NET service contender could not be launched.",
                                "Check the complete multi-file deployment and configured command. No service was replaced.", cause);
                        }
                        lastSpawn = options.Clock.GetElapsedTime(started);
                    }
                }
                await Task.Delay(ServiceTiming.PollInterval, options.Clock, ct).ConfigureAwait(false);
            }
            throw new ServiceLifecycleException(ServiceFailure.StartupTimeout, "Timed out waiting for a ready compatible service.",
                "Inspect the server's starting/stopping state and startup diagnostics. No process was terminated or registration removed.");
        }
        finally
        {
            foreach (var child in children) child.Dispose();
        }
    }

    /// <summary>Requests shutdown only from the authenticated registered instance. Never signals a PID.</summary>
    public static async Task StopAsync(string? registrationFile = null, CancellationToken ct = default, TimeProvider? clock = null)
    {
        var file = GetRegistrationFile(registrationFile);
        var info = await ReadAsync(file, ct).ConfigureAwait(false);
        if (info is null) return;
        var result = await ProbeAsync(new ServiceEndpoint(info.Url, info.Password), info, new ServiceDiscoveryOptions { Version = null }, ct).ConfigureAwait(false);
        if (result.Error is not null) throw result.Error;
        if (result.Status is null)
            throw new ServiceLifecycleException(ServiceFailure.InvalidIdentity, "Cannot authenticate the registered .NET instance.", "Verify its identity before attempting shutdown. Registration was left untouched.");
        await StopRegisteredAsync(file, info, ct, clock ?? TimeProvider.System).ConfigureAwait(false);
    }

    private static async Task StopRegisteredAsync(string file, ServiceInfo info, CancellationToken ct, TimeProvider clock, TimeSpan? stopTimeout = null)
    {
        if (await ReadAsync(file, ct).ConfigureAwait(false) != info)
            throw new ServiceLifecycleException(ServiceFailure.InvalidIdentity, "Registration changed before cooperative shutdown.", "Rediscover the service. No replacement was attempted.");

        using var http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(info.Url), "/api/service/stop"));
        new ServiceEndpoint(info.Url, info.Password).ApplyAuth(request);
        request.Headers.Add("X-OpenCode-Service-ID", info.Id);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ServiceLifecycleException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ServiceFailure.Unauthorized : ServiceFailure.InvalidIdentity,
                "The registered instance rejected cooperative shutdown.", "Recheck the instance identity and credentials. No PID was signalled or registration removed.");

        var started = clock.GetTimestamp();
        while (clock.GetElapsedTime(started) < (stopTimeout ?? ServiceTiming.StopTimeout))
        {
            var current = await ReadAsync(file, ct).ConfigureAwait(false);
            // A concurrent contender may already have acquired the released lease
            // and published its own registration. Never wait on or stop that owner here.
            if (current is not null && current != info) return;
            if (current is null)
            {
                try
                {
                    var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    await using var leaseLifetime = lease.ConfigureAwait(false);
                    return;
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33) { }
            }
            await Task.Delay(ServiceTiming.StopPollInterval, clock, ct).ConfigureAwait(false);
        }
        throw new ServiceLifecycleException(ServiceFailure.ShutdownTimeout, stopTimeout is null
                ? "The service did not finish cooperative shutdown within five seconds."
                : "The older dotnet-local service did not finish cooperative shutdown within thirty seconds.",
            "Inspect outstanding requests and server diagnostics. Forced termination is unsupported; no process was killed.");
    }

    private static async Task<PersistentPtyHandoff?> PreparePtyHandoffAsync(ServiceInfo info, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(info.Url), "/api/experimental/persistent-pty/handoff"));
        new ServiceEndpoint(info.Url, info.Password).ApplyAuth(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        // Builds predating the native persistent-PTY backend have no handoff route.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ServiceLifecycleException(ServiceFailure.RecoveryUnsupported, "The existing server could not prepare persistent terminal handoff.",
                "Resolve the persistent terminal error before replacing this server. It was not stopped.");
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var bodyLifetime = body.ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync(body, PersistentPtyHttpJsonContext.Default.PersistentPtyHandoffResponse, ct).ConfigureAwait(false)
            ?? throw new ServiceLifecycleException(ServiceFailure.InvalidIdentity, "The persistent terminal handoff response was invalid.", "The current server was not stopped.");
        return result.Handoff;
    }

    private static async Task<bool> IsIdleAsync(ServiceInfo info, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(info.Url), "/api/session/active"));
        new ServiceEndpoint(info.Url, info.Password).ApplyAuth(request);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object && !data.EnumerateObject().Any();
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    }) { Timeout = ServiceTiming.RequestTimeout, MaxResponseContentBufferSize = 16 * 1024 };

    private static async Task<ServiceInfo?> ReadAsync(string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var info = JsonSerializer.Deserialize<ServiceInfo>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            if (info is null || string.IsNullOrWhiteSpace(info.Id) || string.IsNullOrWhiteSpace(info.Version)
                || string.IsNullOrEmpty(info.Password) || info.Pid <= 0
                || !Uri.TryCreate(info.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1"
                || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return null;
            return info;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static async Task<ProbeResult> ProbeAsync(ServiceEndpoint endpoint, ServiceInfo? info, ServiceDiscoveryOptions options, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("A server must be an HTTP(S) origin without user information, query, fragment, or path.", nameof(endpoint));
        using var http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/health"));
        endpoint.ApplyAuth(request);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(null, new ServiceLifecycleException(ServiceFailure.Unauthorized, "The server rejected the supplied service credentials.", "Verify the endpoint and its credentials. Do not delete registration to force replacement."));
            ServiceIdentityHealth? health;
            try { health = await response.Content.ReadFromJsonAsync<ServiceIdentityHealth>(ct).ConfigureAwait(false); }
            catch (JsonException)
            {
                return InvalidIdentity($"The health endpoint returned HTTP {(int)response.StatusCode} with an empty or invalid health response.");
            }
            if (health is not { Healthy: true, Pid: >= 0 } || string.IsNullOrWhiteSpace(health.Version)
                || (health.BuildId is not null && !IsBuildId(health.BuildId))
                || (info is not null && (health.Application != Application || health.Channel != OpenCodeChannel.Name
                    || health.Id != info.Id || health.Pid != info.Pid || health.Version != info.Version
                    || (info.BuildId is not null && health.BuildId != info.BuildId)
                    || (info.BuildTimestamp is not null && health.BuildTimestamp != info.BuildTimestamp))))
                return InvalidIdentity();
            var state = response.StatusCode switch
            {
                HttpStatusCode.OK => ServiceState.Ready,
                HttpStatusCode.InternalServerError => ServiceState.Failed,
                HttpStatusCode.ServiceUnavailable => health.State == "stopping" ? ServiceState.Stopping : ServiceState.Starting,
                _ => (ServiceState?)null
            };
            if (state is null) return InvalidIdentity();
            var expectedBuild = OpenCodeChannel.IsLocal ? ApplicationBuild.Id : options.ExpectedBuildId ?? (info is not null ? ApplicationBuild.Id : null);
            var compatible = (options.VersionPredicate?.Invoke(health.Version) ?? (options.Version is null || options.Version == health.Version))
                && (expectedBuild is null || expectedBuild == health.BuildId)
                && (!OpenCodeChannel.IsLocal || (health.Version == ApplicationBuild.Version && health.BuildTimestamp == ApplicationBuild.Timestamp
                    && health.Channel == OpenCodeChannel.Name && health.Application == Application));
            return new(new ServiceStatus(endpoint, health.Version, health.Pid.Value, health.Id, state.Value, compatible, health.BuildId, health.Channel, health.BuildTimestamp), null);
        }
        catch (HttpRequestException cause)
        {
            return new(null, new ServiceLifecycleException(ServiceFailure.Unreachable, "The server health endpoint could not be reached.", "Verify the selected server and its listener. No process was stopped.", cause));
        }
        catch (JsonException) { return InvalidIdentity(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, new ServiceLifecycleException(ServiceFailure.ProbeTimeout, "The server health probe timed out.", "Inspect the selected instance. Forced recovery is not supported."));
        }
    }

    private static ProbeResult InvalidIdentity(string? reason = null) => new(null, new ServiceLifecycleException(ServiceFailure.InvalidIdentity,
        reason ?? "The health response does not match the expected service identity or protocol.", "Verify the server URL, registration, and protocol version. Refusing automatic replacement."));

    private sealed record ProbeResult(ServiceStatus? Status, ServiceLifecycleException? Error);
    private static bool IsBuildId(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ServiceIdentityHealth(bool Healthy, string? Application, string? Id, string Version, int? Pid, string? State,
        [property: JsonPropertyName("buildID")] string? BuildId, string? Channel, long? BuildTimestamp);
}
