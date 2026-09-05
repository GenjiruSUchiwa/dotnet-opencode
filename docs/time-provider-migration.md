# TimeProvider migration

Application-controlled time reads, delays, deadlines and periodic schedules now use
the owning host's BCL `TimeProvider`. The full CLI dependency graph compiles with
the pinned .NET 11 SDK. This is a source/compilation result, **not demonstrated
runtime, timer, disposal, calendar or provider conformance**. No timing probes,
tests, FakeTimeProvider package, app startup or database operations were run.

## Composition and ownership

- `ServerHost.CreateApp` / `CreateStandaloneApp` accept a clock and choose
  `TimeProvider.System` when omitted. That one choice is registered in DI and
  passed to the database and managed-service lifetime.
- `SdkHostOptions.Clock` is the embedded host's explicit choice, defaulting to
  System. SDK composition registers and passes that same instance. Borrowed SDK
  services remain caller-owned; callers must compose them with the same provider.
- `CliApplication.InvokeAsync` / `CreateRoot` capture a clock in command actions.
  Run, Stats, API, authentication, service lifecycle and TUI entrypoints pass it
  onward. Building the command graph does not read the clock or start a timer.
- `ServiceDiscoveryOptions.Clock` (also inherited by ServiceStartOptions) controls
  client-side startup/spawn/stop polling. A separately spawned server owns its own
  process clock; a TimeProvider object is not serialized across the process boundary.
- `IDatabase.Clock` exposes the host choice to stores and event transactions.
  The default interface value is System for independently implemented databases.
  `SqliteDatabase` accepts the provider explicitly. No schema, persisted column,
  data contract or persistence engine was replaced.
- Location composition passes the provider to tool registries, filesystem search,
  formatters, MCP, forms, shell services, PTY tickets and persistent-daemon clients.
  Session operations use their SessionStore's provider. Static event publication
  helpers take an explicit provider, not an ambient/global clock.
- `OpenTuiHost.Clock` selects the explicit provider, an existing DI registration,
  or System. Renderer, input, clipboard and child components receive that choice.
  Caller-provided services keep their original ownership. The small ClockServices
  adapter supplies TimeProvider without mutating or disposing the caller's DI graph.
  External service graphs should use the same provider; changing clocks during a
  live host's lifetime is not a supported clock-switching feature.

There is no mutable global clock, AsyncLocal clock, or replacement clock interface.
The optional System defaults preserve standalone public construction. Production
composition passes the host-selected provider rather than relying on leaf defaults.

## Reusable-library boundary

`Runtime.Time` is an application-neutral, BCL-only project. It has no OpenCode,
Schema, persistence, network, or UI dependency. It contains two reusable extensions:

- `GetTimestampMilliseconds()` converts a provider timestamp into a **transient
  monotonic millisecond coordinate**, preserving the units used by former
  TickCount64 consumers. It is never used for persisted or wire timestamps.
- `CreateLinkedCancellationTokenSource(...)` constructs a provider-backed BCL CTS
  with an initially infinite timer, then owns registrations for the caller/host
  tokens. In .NET 11, subsequent `CancelAfter` calls change that existing ITimer.
  Cancellation, CancelAsync and timeout validation remain BCL operations.
  Registration disposal precedes base CTS disposal, so linked callbacks finish
  before the provider timer/source is disposed. No independent timeout callback,
  polling loop, fire-and-forget timeout task or global cancellation map is added.

Core, Server, CLI and OpenTui.Blazor use those extensions. OpenTui.Native and Client
use BCL TimeProvider overloads directly and need no Runtime.Time dependency. Neither
generic OpenTui project acquires an OpenCode-branded/domain dependency.

## Complete source inventory

The audit covered `src` C# and Razor sources, including target-typed timer/source
construction and cancellation-only waits, not just matching `UtcNow` calls.

| Area | Migrated application-controlled uses |
| --- | --- |
| Database/bootstrap/migration runner | Journal completion epochs, credential/session writes; source SQL statements remain untouched |
| Durable events and projections | EventTransaction publication time; assistant, inbox, instruction, mutation, archive, skill, shell, compaction and move update epochs |
| Projects/worktrees/locations | Discovery, reconcile, mutation, creation and notification timestamps |
| Sessions | Retry-After date arithmetic, retry schedule epochs/delays, physical-attempt settlement deadlines, compaction, title and subagent cleanup budgets, coordinator settlement |
| Instructions and statistics | Current local date text; UTC default statistics boundary. Existing parsing, local calendar grouping, DST rules and display conversions stay in place |
| Jobs | Start/completion/cancellation epochs and timed WaitAsync; cancellation-only joins remain unchanged |
| Tools/filesystem | Monotonic 500 ms registry debounce and 10 s index refresh, ripgrep/formatter process deadlines, shell-process timeout, webfetch/websearch deadlines and current year |
| Code Mode | Outer invocation deadline and owner-thread monotonic execution budget only; no change to guest Date or provider execution semantics |
| Integrations/OAuth/MCP | Attempt expiry/removal timestamps, scrubber PeriodicTimer, startup/catalog/execution deadlines, callback cleanup, token restoration time and existing OAuth polling |
| Shell/PTY | Shell event epochs, resettable process expiry, UTC file-retention cutoff/schedule, persistent-daemon request/poll/handoff deadlines, ticket/form clocks, WebSocket close deadlines |
| Server | Raw/plugin/permission/MCP/PTY event timestamps, SSE heartbeat, service monitor, readiness budgets and clock registration |
| Client | Monotonic managed-service startup, contender spacing and cooperative stop polling |
| CLI command paths | Login selection/bind polling and expiry, Run fallback/output epochs, Stats request budgets and current boundary, standalone/server/client composition |
| CLI TUI | Handshake/request/event-idle deadlines, reconnect backoff and grace/spinner, tab motion/hover/click/hold, picker debounce/spinner, integration polling/cancel, state-file lock retry, stash epochs/relative display, view-report retry |
| OpenTui.Blazor | Render-loop delay, keymap/selection coordinates, terminal startup/escape deadlines, selected provider injection, managed WASI clock callback |
| OpenTui.Native | Managed clipboard operation retirement/provider polling; native timeout implementation remains native-owned |
| Transport.Pipelines | Audited: no application clock, delay, deadline, Timer or PeriodicTimer. Its waits are cancellation-only semaphore ownership; no artificial clock dependency was added |

Every application `Task.Delay` and `PeriodicTimer` uses the provider overload.
Timed `Task.WaitAsync` uses its TimeProvider overload. Existing timed CTS
constructors use `(TimeSpan, TimeProvider)`; rescheduled linked deadlines use the
provider-backed linked source. Cancellation-only CTS, Task.WaitAsync, semaphore
waits and I/O cancellation were not converted into timers.

## Units and behavior retained

- Persisted/public `created`, `updated`, expiry and retry values remain UTC Unix
  milliseconds from `GetUtcNow().ToUnixTimeMilliseconds()`. No Ticks/default-DateTime
  replacement or new storage format is introduced.
- Elapsed budgets use GetTimestamp/GetElapsedTime. Transient UI/cache millisecond
  counters share one provider coordinate; no comparison mixes them with UTC epochs.
- The managed WASI callback retains its original precision: realtime is UTC Unix
  milliseconds multiplied by 1,000,000; monotonic time uses timestamp/frequency
  converted to nanoseconds. No WASM grammar or native binary was rewritten.
- Retry jitter still uses the existing Random.Shared distribution, ceilings, caps
  and backoff arithmetic. Calendar/DST parsing and file mtime interpretation were
  not replaced with elapsed-clock arithmetic.
- Zero/infinite timeout policy, foreground/background distinctions, cancellation
  reason classification, user-versus-shutdown execution claims, and cleanup waits
  retain their original control flow. No timeout wrapper abandons an external I/O
  operation or changes native process ownership.
- Tab animation initialization occurs after the host clock is injected and before
  parameter projection/timer startup. No-op updates and nullable animation-start
  markers remain intact. Highlighter creation and clipboard reads remain lazy.
  Production grammar leases are pooled by the explicitly supplied TimeProvider,
  preventing one differently composed host from capturing another host's clock.

## Exact retained exceptions

1. **Schema ID generation:** `OpenCode.Schema/Identifier.cs` retains its static
   `DateTimeOffset.UtcNow` fallback and explicit timestamp overload. Prefixes,
   timestamp/counter ordering, byte layout and modulo-based random-character
   distribution are unchanged. Schema gets no mutable clock dependency.
2. **SQL migration text:** clocks embedded in
   `DatabaseBootstrapSchema.Generated.cs` and source migration statement assets are
   source-owned SQL, not application clock calls. Only the C# completion stamping
   around those statements uses the host clock.
3. **JS Date and regex internals:** Jint's Date implementation and regex engine's
   internal timeout clock remain unchanged. Only the surrounding managed Code Mode
   admission/execution deadlines are injected. No guest-visible TimeProvider exists.
4. **Native/third-party internals:** native Zig/OpenTUI clipboard operation
   timeouts, PTY daemon timestamps and library-internal WASM behavior remain with
   those implementations. Managed polling and the managed WASI shim were migrated.
   MCP SDK initialization/stdio shutdown and newly obtained token timestamps are
   library-owned; the installed MCP 2.2.0 public metadata exposes no TimeProvider
   option for those internals. Existing restored-token calculations use the clock.
5. **External API timeout mechanisms with no provider overload:** unchanged
   HttpClient.Timeout settings include ServiceDaemon's 2 s requests,
   ServerHost's 2 s readiness request, ServerReadinessClient's 15 s request, and
   WellknownTransport's 30 s request. Existing DI-created/default HttpClient and
   framework/network/SQLite busy-timeout mechanisms remain BCL/library-owned.
   The pinned .NET 11 HttpClient reference API has no TimeProvider surface.
   These were not replaced with racing Task.Delay/WaitAsync wrappers or transport
   rewrites merely to claim injection. Existing infinite HTTP timeouts stay infinite.

These exceptions mean a supplied clock controls application scheduling, not all
clocks in the OS, network stack, native libraries or language runtimes. Source
searches find no other direct application Now/UtcNow, Stopwatch, TickCount,
Thread.Sleep or unconverted Timer usage.

## Verification

Used SDK `11.0.100-preview.7.26381.103` from the repo-local `.dotnet` directory:

```text
.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj -f net11.0
  --artifacts-path C:\tmp\opencode\time-provider-build
  -p:NuGetAudit=false -p:OpenApiGenerateDocuments=false -v:minimal
```

The full CLI graph includes Schema, Protocol, Core, Client, Server, SDK, both
OpenTui projects, Transport.Pipelines and Runtime.Time. The full graph passed with
0 warnings and 0 errors. OpenAPI document generation/app boot was disabled.
Validation consisted of source/package/reference-API inspection and compilation
only. No tests, timer probes, fake-clock package, DI/app execution, serializer/DB
probes, native/provider/MCP/network verification, commits or delegation were used.
