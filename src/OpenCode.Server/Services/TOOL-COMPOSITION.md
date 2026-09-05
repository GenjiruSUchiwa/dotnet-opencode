# Shared Tool Locations

The daemon calls `AddLocalToolLocations` once before the hosted
`SessionExecutionService` registration. Do not separately construct or register
`PermissionLocationMap`, `StorePermissionLocationFactory`, or another HTTP-only
permission map.

```csharp
builder.Services.AddLocalToolLocations(location => new LocalToolOptions(
    Home: globalHome,
    RipgrepExecutable: resolvedRipgrepExecutable,
    LoadReadInstructions: sessionInstructionsLoad,
    Formatter: formatterForLocation(location)));
```

The daemon supplies absolute paths. `globalHome` must use the same global home
selection as AgentCatalog, including `OPENCODE_TEST_HOME` when applicable. Resolve
the actual ripgrep executable without substituting a shell or downloading one in
this composition. `Formatter: null` is valid only when no formatter is configured.
The options callback runs when a Location is loaded, not during registration.

The current `LocalToolOptions` constructor requires a real `LoadReadInstructions`
delegate. It must load discovered paths through the Session-owned direct durable
synthetic operation, with `instruction.paths` metadata and model-visible-history
deduplication. Do not supply a no-op or a throwing callback: read discovery treats
loader failures as best effort after a successful read. Missing wiring must fail
before tool construction instead.

## ServerHost Binding

`ServerHost` invokes this extension before registering the execution hosted
service. Its lazy options callback uses the same built application service
provider, not another container or permission map. It resolves a registered
`LoadReadInstructions` delegate and rejects Location loading if that service is
absent. The delegate is now bound to the same singleton engine's
`SessionExecutionEngine.LoadReadInstructionsAsync`, which uses the executing
tool's loaded Location and the Session-owned direct durable instruction loader.
The engine is explicitly constructed with the exact factory/map registered by
this extension, not optional null dependencies or another map.

The registration is unconditional. Session-scoped permission hydration resolves
the HTTP adapter and calls `PermissionLocationMap.TryAcquireLoadedAsync`; this
does not call `ToolLocationFactory.CreateAsync` or evaluate local tool options.
An authoritative unloaded Location can therefore return an actual empty pending
list without requiring ripgrep or a read loader to construct tools. A loaded
Location returns its existing permission queue. Closing/failed Locations remain
unavailable rather than being reported as empty. Creation/acquisition of a new
tool Location still validates the actual loader, executable, and formatter.

Home follows AgentCatalog exactly: the full path of `OPENCODE_TEST_HOME`, when
specified, otherwise the operating system's user-profile directory.

Ripgrep resolution is filesystem-only and deferred until a Location loads:

1. If `OPENCODE_DOTNET_RIPGREP` is set, require that exact absolute executable
   file; a missing/invalid override fails rather than falling back.
2. Otherwise search `PATH` for `rg.exe` on Windows or `rg` on Unix, then the
   existing upstream Global.bin path under `<XDG cache>/opencode/bin`.
3. Require an existing file, an `.exe` on Windows, or executable Unix mode bits.
   No shell wrapper, download, installer, version probe, or directory creation is
   performed by this lookup. This does not prove binary identity by execution.

`OPENCODE_DOTNET_RIPGREP` is a .NET host override, not an invented field in the
shared project configuration. Relative cache roots are rejected. The fallback
cache path follows upstream Global.bin naming and is used only if it already
contains the executable; no user-specific installation path is embedded.

The host supplies the actual `LocalFormatter` adapter using the Location and
project directories plus that binary-cache path. Its own unsupported npm
installation/detector guards remain active. No formatter no-op or implicit
installer is substituted, and this wiring does not set runner Tools capability.

The extension registers one `ToolLocationFactory` and one
`PermissionLocationMap`. The factory uses real CatalogLocation project discovery,
retains the authoritative lexical Location key, and rejects workspace placement.
Every Location gets one map-owned `PermissionEventBridge`. Its lifetime ends when
Core closes the permission notification channel, not when an HTTP request ends.

HTTP create/list/get/reply routes acquire leases from this map through
`IPermissionLocationServices`. Request disposal releases only the lease; it does
not discard pending approvals. The hosted adapter closes the map after session
execution drains. Core settles permissions, drains the bridge and leases, and
disposes the Location registry.

The runner must inject these SAME singleton instances:

```csharp
ToolLocationFactory factory;
PermissionLocationMap locations;

await using var lease = await factory.AcquireAsync(locations, session.Location, ct);
var snapshot = await lease.SnapshotAsync(session.Id, selectedAgentId, ct);
// Retain snapshot and lease through the request and its tool-call settlement.
```

Do not use the legacy empty process-global ToolRegistry for request advertising or
execution. This extension does not change runner capabilities or perform model
calls. Advertising and execution remain the runner owner's integration.

Unless the host registers a real `IPermissionGrantStore` first, grants use the
explicit `MemoryPermissionGrantStore`. `PersistentGrants` stays false; always
replies with save resources fail without settling the request. Once/reject work
through the actual queue. Saved-permission list/remove remain unsupported until
there is a durable saved-grant API.

Only compilation and static checks were performed. No application, tool, process
adapter, provider, configuration or database was executed for verification.
