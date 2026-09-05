# Standalone Session skill activation

## Core/Event owner: exact publication dependency

`SessionSkillContracts.cs` defines:

```csharp
public interface ISessionSkillPublisher
{
    Task PublishAsync(SessionSkillActivatedData data, EventId? eventId, CancellationToken ct);
}
```

`SessionSkillActivatedData` has exactly `SessionId`, `Id` (SkillId), `Name`, and `Text`, encoded as `{ sessionID, id, name, text }`. `SessionSkillJsonContext.Default.SessionSkillActivatedData` provides the codec.

Implement this through the existing coordinated Session event-store publication/projector boundary:

- Canonical event: **`session.skill.activated.1`**, aggregate field `sessionID`.
- Append one event, project one `SkillMessage`, advance the aggregate sequence, then notify actual committed observers. Use the existing Session admission/mutation coordination and existence/current-placement checks.
- Project message ID from committed event ID (`evt_…` → `msg_…`), `type: skill`, `skill: event.data.id`, `name`, raw `text`, event metadata when present, and `time.created: event.created`.
- A reused durable event ID is a publication failure. It is **not** prompt/inbox reconciliation, first-admission-wins, replay, overwrite, or successful no-op. Failed publication must not schedule resume.
- No alternate assistant/synthetic/compaction publisher or direct message SQL is a valid substitute.

There was no public native skill publisher when this pass began. No SQL, projector, EventStore/SessionStore edit, fake event family, or default-success implementation was added here. The Core/Event owner must supply the concrete implementation before the endpoint can be enabled.

## Implemented Core operation

```csharp
new SessionSkillService(existingSessionStore, actualPublisher,
    existingExecutionEngine, hostLifetime);

Task ActivateAsync(SessionId sessionId, SessionSkillRequest input, CancellationToken ct = default);
```

Request fields are exactly `skill`, optional `id: MessageId`, and optional `resume: bool`. There are no args/model/delivery/Location overrides.

The service:

1. Gets the actual Session and its current stored Location.
2. Reads `InstructionCatalog.ListSkillsAsync` at that Location, preserving the existing producer's precedence and complete-source/plugin-readiness behavior.
3. Resolves the exact registered skill ID; unknown IDs raise `SessionSkillNotFoundException` rather than loading a supplied path or falling back to empty text.
4. Publishes raw `skill.Content` and the real ID/name through ISessionSkillPublisher.
5. Unless `resume:false`, schedules the existing **forced** `ResumeHostedAsync` after commit on the host lifetime. It does not substitute advisory WakeAsync or create another model runner. HTTP cancellation does not own this already-scheduled resume.

Resume failures are ignored/logged by type after activation commits, as in source; the existing execution engine owns its events/outcome. `resume:false` commits without model readiness or any resume/wake. Disposing the service cancels/joins its owned resume calls.

### Operation-specific ID and retry behavior

A supplied canonical `msg_…` ID is mapped to `evt_…` using the source leading-prefix replacement. Omitted ID lets the publisher generate a fresh event. The current .NET EventId contract requires `evt_`; a noncanonical legacy message ID that cannot map to that contract is rejected, not silently rehashed/reassigned.

Every invocation resolves the Session and current skill before publication. Reusing an ID after successful activation fails at duplicate-event publication, even if the requested skill/payload/resume mode changed. It does not re-resume an already-committed activation as a retry side effect. Repeating activation without an explicit ID is a new event/message.

### Explicit user versus model tool semantics

Source Session.skill is an explicit authenticated-user operation; it does not perform the model skill tool's permission assertion or manufacture a ToolContext/agent identity. Slash/autoinvoke flags do not block explicit activation. Model SkillTool keeps its existing permission/scanner/renderer behavior separately.

Standalone activation stores **raw registered content**, not SkillTool.ToModelOutput with sampled files/base-directory wrapper. That distinction follows source `core/session.ts`; the current native SessionHistory skill branch already lowers the resulting raw text. The existing typed prompt-attachment picker path remains unchanged and is not used to emulate activation.

Configured/discovered plugins, remote skill pull, and workspace placement not supported by the existing local producer fail explicitly. No filesystem subset is presented as a complete plugin registry.

## Server and Client owner wiring

`Server/Endpoints/SessionSkillEndpoints.cs` supplies:

```csharp
services.AddSessionSkillServices();
// Also register the concrete ISessionSkillPublisher before first service resolution.
app.MapSessionSkillEndpoints();
```

The route is exactly `POST /api/session/{sessionID}/skill`, accepts the canonical request body, derives placement from the Session, and returns 204 after activation publication/scheduling. It uses the existing recording-readiness gate, not model readiness. SessionNotFound and SkillNotFound map to the source 404 errors. Missing publisher/host capability is unavailable, not fake success. Duplicate event failures are not converted to 204 or an invented idempotent result.

No existing SessionEndpoints or ServerHost was edited. The endpoint is separate from `/api/skill` listing.

Client now exposes:

```csharp
Task ActivateSkillAsync(SessionId sessionId, SkillId skill,
    MessageId? id = null, bool? resume = null, CancellationToken ct = default);
```

It uses the existing authenticated Client transport, no automatic retries, no Location override, and no Core runtime dependency. The shared error wrapper retains the SkillNotFound status/raw payload even if the common typed query-error union does not yet include that variant. Public SDK composition remains the Core/SDK owner's task.

## Source mapping

- `packages/protocol/src/groups/session.ts` lines 380–397: exact path/body/no-content/errors.
- `packages/server/src/handlers/session.ts` lines 361–377: Session/skill not-found mapping.
- `packages/core/src/session.ts` lines 438–456: registered lookup, raw content, MessageId/EventId mapping, forced asynchronous resume.
- `packages/schema/src/session-event.ts` Skill.Activated and `core/src/session/message-updater.ts` lines 154–165: canonical event/message fields.
- `packages/core/src/bus.ts` durable publication duplicate-ID rejection: append is not replay.

Verification is pinned local .NET 11 Core/Server/Client builds only, isolated at `C:\tmp\opencode\mcp-finish-pass`. No tests, activation/skill read, live config/filesystem/DB/API/model/process/network execution, project-file edits, or commits were performed. The actual publisher remains the explicit Core/Event dependency; compiling this service does not claim that a missing adapter is operational.

Final full Core, Server, and Client builds each succeeded with **0 warnings and 0 errors**. The initial combined command timed out while building Client; its separate rerun succeeded. No activation or publication was executed to verify behavior.
