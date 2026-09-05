# PTY Host Integration

The basic PTY endpoints and WebSocket transport are implemented. ServerHost now
installs the composition below. No persistent PTY endpoint or daemon behavior is
implemented.

## Current Host Wiring

- Location resolution uses `CatalogLocation.ResolveAsync` with the shared database,
  including its existing project discovery/storage semantics and workspace refusal.
- Shell selection uses `PtyShellSelection.Resolve`, loading current Location config
  for each default-shell creation. The Windows config-priority selection follows
  `core/src/config/plugin/shell.ts` and `core/src/shell/select.ts`: configured shell,
  or SHELL when not configured, then pwsh, powershell, Git Bash, and COMSPEC/cmd.
  Lookup uses PATH plus the existing OpenCode binary cache. Relative configured
  paths and Unix PTYs are explicitly unsupported; no deployment-cwd shell guess
  or executable download is performed.
- Credentials use the same `ServiceLifetime.Authorized` verifier as normal routes.
  CORS additions come only from the .NET service config's `cors` list.
- CORS and WebSocket middleware precede Basic authentication. Only the existing
  `HasPtyConnectTicketURL` predicate bypasses that outer check; readiness remains
  enforced and the handler retains origin validation and single-use consumption.
- No native Location plugin supervisor exists. The flush callback explicitly
  refuses PTY creation instead of pretending plugins completed. List, get,
  update, delete, token and connect do not invoke that creation-only callback.
- Host environment injection is the source default empty map, not an implemented
  plugin hook or shell integration. Creation remains blocked at plugin flush.
- The tool/permission Location notification pump's finalizer awaits PTY map
  invalidation before its Location close completes. Request lease release and
  session movement do not invalidate shared PTYs. PTY-only Locations are closed
  by the map's DI-owned host teardown; no separate Location-dispose HTTP route is
  currently implemented.

`SessionMovement` is also registered as one singleton using the shared database,
session store and permission map, and is passed as the final argument of the
explicit `SessionExecutionEngine` factory. The session endpoint owner controls
move-route exposure; this composition does not duplicate that route.

## Registration

Call `builder.Services.AddLocalPty(new PtyHostOptions(...))` before `Build`.
Supply these real host callbacks:

- `ResolveLocation(directory, workspace, ct)`: the authoritative Location resolver,
  returning `LocationInfo`. It must reject unsupported workspace placement. The
  existing `CatalogLocation.ResolveAsync` is usable by an owner that accepts its
  project resolution/storage behavior; it is not called during build verification.
- `ResolveShell(location)`: the Location's current configured shell resolver, with
  source config priority. Resolution happens on terminal creation, not registration.
- `Authorized(request)`: `request => service.Authorized(request.Headers.Authorization.ToString())`.
  Reuse the existing credential verifier; do not install an always-true callback.
- `FlushPlugins(location, ct)`: the Location plugin-supervisor flush operation.
- `Environment(directory, cwd, ct)`: server PTY environment injection. Its entries
  override client input. The upstream default is an empty dictionary; any shell
  integration supplied by this host must be applied here.
- `Cors`: the server's explicitly configured additional allowed origins.

Registration owns one singleton `PtyLocationMap`, NOT one singleton `PtyService`.
The map creates one runtime per resolved absolute `LocationRef`, preserving
workspace identity and refusing unsupported explicit-workspace placement.
Releasing an HTTP request does not terminate its terminals.

The map installs exactly one `PtyEventBridge` per runtime. It enqueues notifications
without blocking the PTY, serializes them with `PtyEventDefinitions`, and calls
`IEventFeedService.Publish` with the resolved `LocationRef`. The endpoints do not
publish a second copy. An internal failure without an observed process exit code
is logged rather than emitting a fabricated canonical exit code.

## Middleware

Before the existing Basic-auth middleware, add:

```csharp
app.UseCors();
app.UseWebSockets();
```

`AddLocalPty` registers the `opencode-pty` CORS policy; only the PTY route group
requires it. CORS handles browser preflight before credential checking. It follows
the upstream allowlist and 86400-second max age; it does not allow all origins or
enable credentialed cross-origin responses.

Change only the existing Basic-auth condition to:

```csharp
if (!service.Authorized(context.Request.Headers.Authorization.ToString())
    && !PtyRequestPolicy.HasPtyConnectTicketURL(context.Request))
```

Keep the readiness gate intact. The predicate matches only
`^/api/pty/[^/]+/connect$` with a nonempty first `ticket` query value. It does not
consume or validate the ticket. The connect handler checks PTY existence, checks
the source allowed-request-origin policy, then consumes the scoped ticket exactly
once before upgrade. Invalid supplied tickets return empty 403 responses even if
Basic credentials are present. Connections without tickets still require Basic
auth. All CRUD and token endpoints also verify credentials through the injected
policy, independently of the outer middleware.

Ticket issuance requires `x-opencode-ticket: 1` plus an allowed request origin.
Tickets bind PTY ID, exact resolved directory, and workspace identity, expire after
60 seconds, and are single-use. Do not add a generic query-token auth bypass.

## Location Shutdown

The authoritative Location owner must call:

```csharp
await services.GetRequiredService<PtyLocationMap>().InvalidateAsync(locationRef);
```

This marks the entry closing, ends attachments and terminates owned PTYs, drains
the event bridge, then removes the entry. Requests cannot recreate that Location
until close completes. DI disposes the entire map on host teardown. The socket
adapter also observes `IHostApplicationLifetime.ApplicationStopping` and request
cancellation; disconnect detaches the client but does not terminate the PTY.

## Routes And Transport

- `GET /api/pty`: Location envelope containing the real runtime list.
- `POST /api/pty`: `PtyCreateInput`; Location envelope containing created info.
- `GET /api/pty/{ptyID}`: Location envelope containing info.
- `PUT /api/pty/{ptyID}`: `PtyUpdateInput` with optional `title` and `size.cols/rows`.
- `DELETE /api/pty/{ptyID}`: terminate/remove; 204.
- `POST /api/pty/{ptyID}/connect-token`: Location envelope containing ticket.
- `GET /api/pty/{ptyID}/connect`: WebSocket transport.

Location query names are `location[directory]` and `location[workspace]`.
Connect supports `ticket` and safe-integer `cursor`, including `-1` for tail.
Missing PTYs return empty 404 before upgrade. Exit/removal during upgrade closes
the accepted socket with code 4404. Normal exit/removal closes with code 1000.

Replay uses 64-Ki UTF-16-unit chunks followed by a binary frame containing NUL
plus UTF-8 `{"cursor":number}`. Live delivery starts only after replay and metadata
are queued. One writer serializes all outbound text, binary metadata, and close
frames. Input accepts fragmented text or binary UTF-8 messages, drops malformed
UTF-8 messages, and writes actual input bytes. Basic cursors count UTF-16 code units,
not persistent-daemon byte offsets. This is not a terminal emulator/checkpoint API.

## Verification

Build only, with the repository's .NET 11 SDK and isolated artifacts:

```powershell
.\.dotnet\dotnet.exe build src/OpenCode.Server/OpenCode.Server.csproj --artifacts-path C:/tmp/opencode/pty-ws-build -v:minimal
```

No WebSocket connection, server, native PTY call, application child process,
database operation, provider, or tests were run. No commits were made. Runtime
verification remains required, and PTY creation requires a real plugin supervisor.
The host-wiring CLI build attempt compiled Core but was blocked by the concurrent
`CommandHostService` reference to missing `InstructionCatalog.ReadMcpConfiguration`.
