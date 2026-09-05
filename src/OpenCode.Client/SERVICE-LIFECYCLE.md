# Local Service Lifecycle

This is the independently registered `dotnet` channel, not the installed OpenCode
service. Immutable deployment and filesystem naming are described in
[SERVICE-DEPLOYMENT.md](SERVICE-DEPLOYMENT.md).

## Source Mapping

| Upstream source | Native implementation |
| --- | --- |
| `packages/client/src/service-timing.ts:14-23` | `ServiceTiming`: 100 ms polling, 2 s health requests, 5 s initial spawn delay, 30 s maximum spawn delay, 120 s startup deadline, 50 ms stop polling over 5 s. |
| `packages/client/src/service-version.ts:3-8` | `ServiceDiscoveryOptions.Version` or `VersionPredicate`; null version accepts any reported version. The convenience .NET API retains its existing exact-version default. |
| `packages/client/src/promise/service.ts:25-30,157-206` | `InspectAsync`, `DiscoverWithOptionsAsync`, and health probing. Ready, failed, and waiting responses are distinct; managed discovery validates registration identity as well as version. |
| `packages/client/src/promise/service.ts:57-117` | `EnsureWithOptionsAsync`: at most two live contenders, no spawns while an authenticated compatible owner is starting/stopping, bounded backoff after zero-exit contenders, and failure when no contender remains after a nonzero exit. A ready incumbent takes precedence over contender exit errors. |
| `packages/client/src/service-contender.ts:55-67` | Contender exit-code and launch-failure reporting; disposing the process handle never terminates the contender. |
| `packages/server/src/process.ts:71-77,88-135,169-227` | Post-bind registration, asynchronous storage boot, authenticated lifecycle health, failed-state retention, and unavailable application routes until ready. |
| `packages/cli/src/services/service-registration.ts:20-69` | Atomic private registration, ownership monitoring, and owner-aware cleanup while the server retains its election lock. |

## Health And Readiness

Health always carries the upstream identity shape `healthy: true`, `version`, and
`pid`. This boolean identifies a health response; HTTP status determines readiness:

| Status | Meaning |
| --- | --- |
| `200` | Ready. |
| `500` | Boot failed. The authenticated health and cooperative stop routes remain available. |
| `503` | Starting or stopping, with `Retry-After: 1`. |

The .NET response additionally identifies its application, channel, unique
instance, and state. Application routes return `503` while not ready. Storage
boot failure never enables application routes or advertises ready health. Boot
currently establishes the native storage baseline; it is not a claim that all
upstream runner/plugin initialization has been ported.

## Explicit Server Selection

The client-facing boundary for a CLI `--server` option is
`ServiceDiscoveryOptions.Server`. An explicit endpoint bypasses all local
registration lookup and never spawns, snapshots, replaces, or stops a local
service. It accepts the public upstream health contract rather than requiring
the private .NET registration envelope. HTTP redirects are disabled.

`InspectAsync` returns identity, lifecycle state, and `Compatible`, allowing a UI
to report a version mismatch before the user deliberately proceeds. It does not
silently change the version policy. `DiscoverWithOptionsAsync` returns an endpoint
only when ready and compatible. `EnsureWithOptionsAsync` with an explicit server
waits for readiness but throws on incompatible, failed, unauthenticated, or
unreachable endpoints; it never falls back to local startup.

`VersionPredicate`, when provided, takes precedence over exact `Version`.
Setting `Version = null` deliberately disables version filtering, but does not
disable health-contract validation. Managed registration still requires an
authenticated .NET application/instance/PID/version match.

CLI argument parsing is outside this package; callers must wire `--server` to
this boundary rather than call managed `EnsureAsync` after an explicit probe fails.

## Errors And Unsupported Recovery

`ServiceLifecycleException` exposes a `Code` and an actionable `Action` without
embedding credentials. Incompatible owners produce `IncompatibleVersion`, failed
boot produces `StartupFailed`, and three consecutive timeouts against the same
registration produce `RecoveryUnsupported`. The startup deadline produces
`StartupTimeout`, not an unbounded wait.

Unlike upstream's force-recovery path, this implementation never sends PID
signals, kills processes, or deletes an incumbent's registration from the client.
Managed Ensure can make one authenticated cooperative replacement after checking
an empty active-session map and a matching replacement package fingerprint.
Explicit servers and uncertain/busy activity are never automatically replaced.
Cooperative `StopAsync` can
stop a verified starting or failed .NET instance as well as a ready one. It
requires the same instance ID at the stop endpoint. If shutdown exceeds five
seconds it returns `ShutdownTimeout` without escalation; the server may still
complete shutdown afterward.

The server also refuses to overwrite a responding registered endpoint even when
an older instance does not hold the current election lock. Connection refusal
permits a stale registration to be superseded after binding; timeouts, invalid
registration, and responding endpoints do not.

PTY handoff, forced recovery, forced version replacement, legacy PID-only
termination, and raw contender stderr-tail capture are not implemented.
Detached launch remains Windows/Linux only and uses immutable multi-file runtime
snapshots. Startup and recovery errors must not be interpreted as permission to
kill an arbitrary PID or modify another OpenCode channel.

Managed compatibility also requires the compiled `ApplicationBuild.Id` reported
as `buildID` in health/registration. A constant semantic version does not permit
stale-source reuse. Missing old build identity is incompatible. See
`docs/build-identity.md` for deterministic generation, package checks, and the
controlled replacement policy.

## Private Startup Diagnostics

Each contender receives a new `--startup-id` nonce and an absolute
`--startup-report` path under
`<XDG state>/opencode/service-dotnet-startup/<nonce>.json`. The client creates a
private pending record before launch; the managed entrypoint advances it before
host construction. The host records configuration, build, election, listener,
registration, and storage phases. Framework startup errors are captured by a
dedicated logging provider, including when a CLI apphost hosts Server directly.
No redirected child-process pipe is used.

Records use owner-only Windows ACLs or Unix mode `0600`, are atomically replaced,
and live outside mutable build output and immutable deployment snapshots. Readers
permit atomic replacement. Records persist for diagnosis; this code does not
delete another attempt's record. Registration carries `startupID` so an already
registered failed instance can be correlated as well.
The record describes startup, not current process liveness; authenticated health
remains the authority for the instance's current lifecycle state.

The client accepts a failed report only when its nonce, application, and process
identity match the attempt. `ServiceLifecycleException.DiagnosticPath` identifies
the file, and its visible message includes the safe summary/action. Nonces must
be exact lowercase GUIDs in `N` format. A child can claim only its caller's
`pending`/`launch` record with PID zero; already-claimed records are rejected even
if an operating system reuses a PID. Supplied diagnostic flags are internal
per-attempt arguments, not reusable service configuration.

Missing, malformed, stale, or mismatched records are not displayed as evidence.
An authenticated failed server is not described as having exited. A pending
report after exit means no matching managed failure was captured: verify runtime
loading of the pinned .NET 11 preview and that the packaged executable supports the current startup protocol.
An operating-system launch error reports its numeric code without quoting the
command or its arguments.

Reports contain allowlisted failure categories, exception type names, numeric
error codes, and call-site symbols. They never contain raw exception messages,
formatted logs, environment values, configuration documents, provider bodies,
credentials, or full command lines. Recognized DI diagnostics extract only
compiled type identifiers. Unknown failures remain actionable through phase,
type, and call-site information rather than unsafe message redaction guesses.
DI-message extraction also requires a DI-framework throw site. Temporary-file
cleanup is best-effort, preserves the original error, and never deletes a
published report. `ServiceLifecycleException.Message` includes `Action` for
simple UI error displays; structured UIs should not append the same action again.
