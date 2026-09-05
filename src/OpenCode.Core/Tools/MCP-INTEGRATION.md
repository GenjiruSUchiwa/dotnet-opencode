# Shared MCP Location Runtime

`ToolLocationFactory` now constructs exactly one
`new McpRuntime(info.Directory, registry, permission, options.McpForms)` after builtin registration.
The private `ToolLocationState` owns that runtime with the same registry and
permission service used by all tool leases. Existing daemon factory construction
does not require a new argument or a second runtime factory.

## Server creation hook and Forms adapter

Supply these host-facing `LocalToolOptions` members:

```csharp
IMcpElicitationForms? McpForms
Func<LocationInfo, McpRuntime, IDisposable>? McpCreated
```

`local(info)` can resolve the actual Location Forms adapter. The factory passes that
same adapter to the runtime constructor, then calls `McpCreated(info, runtime)`
synchronously before publishing the Location scope or exposing its first lease.
Server attaches its MCP event bridge here and returns the actual subscription.
Do not observe/start servers, reenter the Location map, or create a second runtime
inside the callback. No placeholder bridge or Forms adapter is installed when omitted.

The returned non-null `IDisposable` becomes part of `ToolLocationState`. It survives
all request leases and is disposed exactly once after MCP shutdown, before registry
disposal. This keeps the bridge attached for terminal MCP status changes. The same
ordering applies if construction is cancelled or fails after subscription. A hook
that throws before returning must release its own partial subscription. Both later
cleanup stages still run when an earlier disposal throws.

The Forms service itself remains host-owned. MCP cancels/settles its elicitation
connections during runtime shutdown; the callback's lifetime can also own a
Location-specific Forms subscription when the host needs one. Do not dispose a shared
Forms service per request. No Core Event or Server dependency is introduced.

`ToolLocationLease.Mcp` exposes that exact shared instance. The runtime itself
installs its stable tool transform once; the factory does not copy MCP registrations
or re-register them on requests. `ObserveAsync` refreshes the runtime's catalog and
awaits registry reload without changing transform precedence. Later plugin overrides
therefore remain later overrides.

## Request Ordering

The runner's readiness and execution paths now use:

```csharp
var observation = await lease.Mcp.ObserveAsync(configuration, ct);
var snapshot = await lease.SnapshotAsync(session.Id, agent.Id, ct);
var source = McpInstructionSource.FromObservation(observation, agent);
// Pass source as mcp: to SessionStore.SelectInstructionsAsync at both boundaries.
```

Configuration normalization/precedence belongs to the existing instruction/config
adapter and `McpRuntime.Configure`, not another implementation inside ToolFactory.
SnapshotAsync does not observe, connect, or re-register MCP a second time. It rejects
capture before `Mcp.SettledObservation` exists rather than silently exposing a
partial native-only catalog. No empty observation substitutes for missing discovery.

Use the returned observation for that request's instructions. The runtime's
`SettledObservation` is a shared latest read model and may advance on another request.
Server failures and needs-auth statuses remain in `observation.Servers`; lack of
discovered tools does not erase those statuses. The instruction adapter owns
permission-filtered guidance and unavailable-source behavior.

## Lifetime and Verification

Releasing a request lease does not dispose MCP or its bridge. Location teardown
disposes MCP, then the creation-hook subscription, then the registry. Later cleanup
stages still run if an earlier stage throws.
The official MCP SDK owns connection/stdio process cleanup. No server/process was
started to verify this wiring.

Readiness/execution code and the MCP runtime remain separately owned; Tools only
supplies their shared Location construction, lifetime and exact `.Mcp` property.
OAuth, unsupported transports and CodeMode behavior remain the runtime/runner's
explicit capability boundaries. This integration does not fake those capabilities.

The current hook pass is verified with isolated Core compilation only; no Server,
MCP connection, Forms request, process, database, provider or test is run.
