namespace OpenCode.Client;

public enum ServiceState { Starting, Ready, Failed, Stopping }

public enum ServiceFailure
{
    InvalidConfiguration,
    Unreachable,
    ProbeTimeout,
    Unauthorized,
    InvalidIdentity,
    IncompatibleVersion,
    IncompatibleBuild,
    StartupFailed,
    ContenderFailed,
    RecoveryUnsupported,
    StartupTimeout,
    ShutdownTimeout
}

public sealed class ServiceLifecycleException(
    ServiceFailure code, string message, string action, Exception? inner = null, string? diagnosticPath = null)
    : InvalidOperationException(message.Contains(action, StringComparison.Ordinal) ? message : message + " " + action, inner)
{
    public ServiceFailure Code { get; } = code;
    public string Action { get; } = action;
    public string? DiagnosticPath { get; } = diagnosticPath;
}

public record ServiceDiscoveryOptions
{
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public string? RegistrationFile { get; init; }
    /// <summary>An explicit server bypasses local registration and never starts, replaces, or stops a service.</summary>
    public ServiceEndpoint? Server { get; init; }
    /// <summary>Required exact version. Set null to accept any reported version.</summary>
    public string? Version { get; init; } = ServiceDaemon.DefaultVersion;
    /// <summary>When supplied, this predicate replaces the exact version requirement.</summary>
    public Func<string, bool>? VersionPredicate { get; init; }
    /// <summary>Optional build requirement for explicit servers; managed discovery defaults to this application's compiled fingerprint.</summary>
    public string? ExpectedBuildId { get; init; }
}

public sealed record ServiceStatus(
    ServiceEndpoint Endpoint, string Version, int Pid, string? Id, ServiceState State, bool Compatible,
    string? BuildId = null, string? Channel = null);

internal static class ServiceTiming
{
    // packages/client/src/service-timing.ts
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan SpawnDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxSpawnDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
}
