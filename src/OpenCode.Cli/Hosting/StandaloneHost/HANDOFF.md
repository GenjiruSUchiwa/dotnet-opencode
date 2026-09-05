# Standalone host: required Server-owned composition hook

Status: the Server owner implemented CreateStandaloneApp(password), and the CLI
now supplies StandaloneHostLease plus Stats/Run integration. The previous blockers
below are retained as the source/ownership rationale, not a request for a second
factory. See README.md for the current lease API and TUI-owner handoff.

## Confirmed source behavior

- `packages/cli/src/services/standalone.ts` starts a scoped self-command child
  with serve --stdio --port 0. It generates 32 random credential bytes, overrides
  inherited password selection and owns the stdin pipe as a lifetime lease.
  The first stdout line supplies the actual bound URL. Source drains stdout until
  shutdown; stdin EOF, including parent process death, terminates the child.
- `packages/cli/src/server-process.ts` uses managed configuration/registration only
  in service mode. Stdio uses loopback by default and the ordinary selected channel
  database/configuration, not a new empty memory database or copied production DB.
  The source removes the lease password from the environment before tools start.
- `packages/server/src/process.ts` installs restart continuity only when a managed
  lifecycle is supplied (lines 104–108). Stdio supplies none: it does NOT sweep
  durable claims in shared storage. Its authenticated health reports actual boot
  readiness; readiness is not inferred merely from allocation of a URL or socket.
- ServerConnection rejects --server combined with --standalone.

The requested native implementation is an owned in-process .NET host rather than
the source child process. Its process-death lifetime ends with the CLI process;
normal cancellation/disposal must close only that host's listener and owned work.
It must not kill processes by name or stop the elected service.

## Why the managed public factory must not be used

ServerHost.CreateApp unconditionally:

1. Begins managed startup diagnostics and reads service-dotnet.json settings.
2. Builds ServiceLifetime, whose StartingAsync acquires an election lock and checks
   the incumbent registration/process.
3. Writes the persistent service password/config and registration after binding.
4. Opens the channel database and invokes SessionExecutionService.StartRecovery.

HealthEndpoints and authorization also depend on that internal ServiceLifetime.
CreateApp(args, port: 0, registrationFile: anotherPath) is therefore NOT a private
host: it still mutates managed state and asserts recovery rights. Changing process
environment, reflective removal of hosted services, or copying the entire Server
service graph into CLI would not be a safe public composition boundary.

## Implemented public hook (Server owner)

The landed signature:

```csharp
public static WebApplication CreateStandaloneApp(string password);
```

Required contract:

- Reuse the real Server API/service composition; bind IPv4 loopback, port 0.
- Require the injected nonempty credential for ordinary routes and health, using
  the existing basic-auth username and ticket rules. Never source the private
  credential from shared config or place it in process environment/argv/logs.
- Do not register managed election/registration/config-writer/incumbent-monitor
  services. Do not consume or clear a managed PTY handoff from shared environment.
- Reuse normal dotnet-channel data selection and schema/readiness guards. No new
  temporary database or migration/import bypass; no cross-channel access.
- Disable all startup claim/job/shell/subagent recovery sweeps. Private lifetime
  does not establish restart ownership over the shared dotnet database.
- StartAsync must fail on initialization failure and complete only when the real
  API is ready. Expose the actual ephemeral address through IServerAddressesFeature
  and the actual native version/build/instance readiness through authenticated health.
- Shutdown/disposal closes only this app's requests, services and owned jobs.
  SessionRunCoordinator is process-global; private disposal must not interrupt all
  process sessions. Existing no-recovery owned-work shutdown may be reused after
  the Server owner verifies that composition.
- Avoid ConsoleLifetime handlers that outlive or stop the CLI, and avoid listener
  log output contaminating stats --json stdout. Route permitted logs to the existing
  diagnostic sink/stderr policy without exposing credentials.

If readiness cannot be guaranteed by StartAsync, return a typed handle exposing
the WebApplication and a readiness Task instead. That is preferable to guessed
health polling or exposing ServiceLifetime internals to CLI.

## CLI integration now implemented

StandaloneHostLease generates the ephemeral secret, creates/starts the private app,
obtains/validates its real loopback bound address, constructs a ServiceEndpoint,
and returns an IAsyncDisposable owned lease. Startup failure/cancellation disposes
the partially created host; disposal is idempotent and awaits owned shutdown.
No ServiceDaemon discovery/ensure/stop/election call belongs in this branch.

StatisticsCommand and RunCommand acquire the lease only for explicit --standalone,
use their existing typed HTTP paths, and release clients before the host.
Managed/explicit-server behavior and source option exclusivity stay unchanged.
The reusable lease is ready for the TUI owner; InteractiveTui was not edited.

## Verification restriction

The original blocked pass read source only. The integration pass uses pinned
.NET 11 full-CLI compilation with OpenApiGenerateDocuments=false. No factory,
listener, DI startup, database, service operation, health request, runtime command,
process action or test was executed for verification. Build evidence and remaining
runtime limitations are recorded in README.md.
