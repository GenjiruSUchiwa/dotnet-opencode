# Owned embedded SDK host

`OpenCodeClient.CreateAsync()` now creates a real tool-capable host. It does not
fall back to a text-only engine. The existing string/path constructor also composes
the host; CreateAsync additionally opens the existing database boundary to apply its
strict bootstrap/schema gate before returning.

```csharp
await using var client = await OpenCodeClient.CreateAsync(
    new SdkHostOptions { DatabasePath = ":memory:", RipgrepExecutable = @"C:\tools\rg.exe" },
    cancellationToken);
```

The path overload preserves the existing channel-database default when omitted.
Use `:memory:` explicitly for isolated storage. Existing databases are not silently
migrated, repaired, or cleared. Injected-constructor resources remain host-owned and
are not disposed by OpenCodeClient.

## Shared composition

The SDK references Server in the permitted direction and reuses its public
`AddLocalToolLocations`, `AddIntegrationServices`, `AddShellServices`, `AddNativeWebSearch`,
`FormLocationServices`, `PermissionLocationServices`, and execution/event services.
Core has no Server dependency. No WebApplication, HTTP listener, service-registration
file, background daemon, or managed-service election is created.

The owned graph includes the existing database/stores, ProjectDiscovery-backed tool
Location resolver, permission map, SQLite permission grants, actual Forms service,
MCP/OAuth/integration observation, Code Mode, anonymous WebFetch transport, read
instruction callbacks, formatter, shell runtime/jobs, Question/Subagent leaves,
generic JobRuntime/JobBackgroundStore, title generation, movement, snapshots/revert,
and shared Session execution. It does not copy model execution, authorization, form,
job, or shell algorithms into the SDK.

Ripgrep resolves from explicit options/environment, PATH, or the existing OpenCode
binary cache. Missing executables fail clearly; no downloads, dependency symlinks,
shell substitutes, or silently omitted tools are used. Existing native shell,
filesystem, formatter, plugin and JavaScript-subset limitations still apply.

Native WebSearch providers come from the shared host registration and the actual
Location plugin scopes. The SDK supplies those registered definitions and a direct
`WebSearchPluginSource.ReadyAsync` callback to `LocalToolOptions`; it does not create
a parallel runtime or reacquire `CommandHostService` from inside tool readiness.
The same binding refreshes before model snapshots, so unavailable or disabled
providers do not become a silently advertised fallback tool. Plugin-added,
plugin-updated, and WebSearch update events use the SDK's existing event feed.

## User decisions and observation

- `PendingPermissionsAsync` and `ReplyPermissionAsync` use the loaded authoritative
  Location PermissionService. Persistent grants use the actual SQLite grant store
  unless the host explicitly supplies another implementation.
- `PendingFormsAsync`, `ReplyFormAsync`, and `CancelFormAsync` use the same FormService
  as Question and MCP elicitation. Ownership is checked against the requested Session.
- `FormLocations` exposes the real scoped service for advanced/global form workflows.
  Borrowed Location leases must be disposed by callers.
- `EventsAsync` yields actual published event envelopes through the shared event feed,
  including committed Session events and live permission/form/MCP notifications.
  Subscriber overflow remains an explicit error, not silent event loss. The underlying
  native Session event bus is process-local, not a multi-tenant isolation boundary.
- Policy/tool hooks are explicit options. No approval, answer, or default decision is
  manufactured by the SDK. Consume events and reply from the embedding application.

Additional owned-host conveniences include Jobs, Environment, MoveAsync,
ClearRevertAsync, RequestCompactionAsync, ExportSessionAsync and ImportSessionAsync.
BackgroundAsync uses the shared JobRuntime for both shell and subagent blocking work;
it is a no-op for a known Session with no blocked jobs and rejects unknown Sessions.
ActivateSkillAsync uses the real standalone skill service and canonical
session.skill.activated publisher; it does not emulate activation with a prompt or
model tool call. Its optional supplied MessageId retains duplicate-publication
failure semantics, and resume false does not start model execution.
Archive import uses the real canonical Created/projected-data transaction, not a
raw Session insert fallback. Raw service facades remain advanced
caller-owned operations: await mutations and release leases before disposing the host.

## Identity and lifetime

`SdkHostOptions.Identity` supplies the real client name/user-agent. The default client
is `sdk`; its user-agent identifies OpenCode.Sdk with the actual generated application
build fingerprint, not a fabricated semantic product version. SessionRequestIdentity
supplies canonical Session/project/parent headers to normal, title and Generate
requests. Configured supported request headers retain their established precedence.

SDK requests link to the owned lifetime. Prompt/Resume retain user-interruption
semantics for caller cancellation, while host shutdown records shutdown interruption
and preserves claims. Disposing a paused output enumerator is not required for the
host to settle the underlying cancelled model work.

Engine-instance drain tracking observes the existing coordinator's tasks, including
scheduled advisory wakes. It creates no new execution owner or global registry and
does not scan/interrupt another engine's process-global Sessions. Shutdown cancels
the host, joins request work/jobs/titles and actual owned drains, keeps the event
bridge alive through settlement, then closes shell/Location/integration/form services
and the provider/store resources. Cleanup failures remain errors.

## Recovery is explicit

Construction/CreateAsync never sweeps execution claims or job markers. Only call
`RecoverAfterConfirmedRestartAsync` when the embedding host has independently
confirmed predecessor death and owns the existing managed registration authority.
This opt-in delegates shell notification recovery before the existing combined
subagent/root sweep. It is not an SDK-created cluster lock or proof of ownership.
Unregistered SDK hosts sharing a live database must not invoke recovery.

## Verification

Only the repository-pinned .NET 11 SDK project build with isolated artifacts was
run. No SDK constructor/CreateAsync, configuration evaluation, database bootstrap,
Location acquisition, model/tool/MCP calls, filesystem/process operations, listeners,
network requests, or application workflows were executed for verification. Build
success is not a runtime integration or sandbox-security test.
