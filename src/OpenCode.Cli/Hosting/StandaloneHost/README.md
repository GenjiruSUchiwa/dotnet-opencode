# CLI standalone host lease

`OpenCode.Cli.Hosting.StandaloneHostLease` now uses the real public
`ServerHost.CreateStandaloneApp(password)` composition. Stats and Run accept
`--standalone` through this lease; no unsupported guard or managed-service fallback
remains in those branches.

## Reusable API / TUI integration

```csharp
await using var standalone = await StandaloneHostLease.StartAsync(startupCancellation);
using var client = new SessionHttpClient(standalone.Endpoint);
// Pass the endpoint to the existing TUI/client composition as appropriate.
// Await that consumer inside this scope, with its own cancellation token.
```

- `StartAsync(CancellationToken)` returns only after the public app StartAsync
  completes. The Server-owned StandaloneLifetime marks readiness after validating
  storage and the bound listener; no guessed readiness or fixed-port probe is used.
- `Endpoint` is the actual `http://127.0.0.1:<ephemeral-port>` origin from Kestrel's
  IServerAddressesFeature plus the private credential. Exactly one loopback origin
  with a nonzero port is required. It contains authentication material: do not log
  the record, publish it in service registration, or put its password into URLs.
- `Stopping` exposes this app's ApplicationStopping token for consumers that need
  to react to host shutdown. It is not a process-global/elected-server token.
- `DisposeAsync()` joins one owned shutdown Task across repeated calls. It awaits
  app StopAsync and always attempts app DisposeAsync, even if stopping fails.

The StartAsync token controls startup. Once returned, the caller owns an
await-using scope: cancel/close its clients, then dispose the lease. It intentionally
does not register a callback that immediately kills the listener when a Run token
is cancelled; Run must first be able to send its Session-specific interrupt. A TUI
owner should likewise finish its client shutdown before leaving the lease scope.
Validate --server/--standalone exclusivity before acquisition. Program's TUI
argument parser now accepts tui --standalone and root --standalone, validates
exclusivity before acquisition, and awaits InteractiveTui.RunAsync with the lease
endpoint inside the lease scope. InteractiveTui itself was not changed.

## Authentication, storage and ownership

The lease generates 32 random bytes encoded as unpadded base64url, matching the
source lease credential strength/encoding. The credential is passed directly to
CreateStandaloneApp. It is never placed in environment variables, argv, config,
registration files or logs; inherited passwords cannot override it. The existing
ServiceEndpoint applies Basic authentication to actual HTTP requests.

The Server hook selects the normal dotnet-channel storage and preserves its schema
and initialization gates. A private listener is not a new blank database. It does
not imply ownership of persistent work left by another process. The hook excludes
managed registration/election, config writes, incumbent monitoring, PTY restart
handoff consumption and recovery sweeps. This CLI lease adds none of those actions.
It never calls the managed CreateApp, ServiceDaemon.Ensure/Discover/Stop, process
enumeration/termination, a Bun/Node/official launcher, or a direct data shortcut.

Normal disposal delegates owned request/job settlement to this WebApplication's
existing hosted services. It does not enumerate SessionRunCoordinator's global
active set or issue a global interrupt. Only the private instance is stopped.
Startup/address validation failure also attempts owned StopAsync/DisposeAsync;
if startup and cleanup both fail, both errors are retained in an AggregateException.
No forced process kill or newly invented shutdown timeout is added.

## Command behavior

Stats declares the lease outside its HTTP client scope, so its client and request
tokens dispose before the host on success, API failure or cancellation. Its
StatsAsync/CurrentProjectAsync calls and report formats remain unchanged.

Run keeps the private host alive through cancellation handling so its existing
Session-specific InterruptAsync can run. Then it closes the HTTP/SSE client and
awaits lease disposal. Cleanup failure is reported as a command failure, not a
successful private shutdown. Run does not upload a managed Session environment
when standalone, matching source ServerConnection's absent managed service handle.
Empty-input rejection, absence of a fixed two-minute cap, and the explicit --auto
headless permission policy are unchanged. Global-form ownership handling is not
broadened by this change. The later Run pass adds second-SIGINT force exit and
tool-specific run text callbacks; the first-interrupt owned shutdown is unchanged.

Both command parsers reject --server combined with --standalone before any factory
call. Stats/Run help still does not acquire a lease. Program also rejects duplicate
or combined explicit-server/standalone TUI arguments before opening a host. With
no TUI connection flags, it still passes null to InteractiveTui and preserves the
default managed behavior. Normal TUI/client disposal completes before lease disposal.

## Source differences and evidence

Upstream CLI standalone uses a scoped child process with a stdin ownership pipe.
This requested native counterpart is an in-process owned WebApplication. CLI
process death necessarily ends its listener, without a child to orphan; graceful
cancellation/disposal uses the managed scope rather than stdio EOF/SIGTERM/kill.
No equivalence of forced termination, native callbacks or storage recovery is
claimed from compilation. The Server hook routes ordinary logs to stderr, not
the Stats/Run JSON stdout stream; source's child-log suppression differs.

The final pinned .NET 11 full-CLI isolated build passed with zero warnings and
zero errors, with OpenApiGenerateDocuments=false. The first attempt reported 15
missing prompt-stash members in other-owned OpenCodeApp.Keybindings.cs and
OpenCodeApp.razor.cs; those errors cleared before final validation without edits
here. No app factory, DI startup, listener, CLI command, database, API/health call,
test, native code or process was executed for verification. No commits or
delegation occurred. Runtime ownership/authentication behavior remains unverified.
