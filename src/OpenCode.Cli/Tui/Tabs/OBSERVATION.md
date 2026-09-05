# Session-bound observation and prompt admission

`SessionClientAdapter` owns one authenticated SSE receiver and a read model per
Session ID. Navigation changes the selected view; it does not move ownership of
outstanding admissions or observations. `Forms.cs` remains separately owned and
uses the same permission/form caches. No second Session executor or command stream
loop is introduced.

## Shared API

```csharp
Task<SessionObservationSnapshot> ObserveSessionAsync(SessionId id, CancellationToken ct = default);
Task<SessionObservationSnapshot> RefreshObservationAsync(SessionId id, CancellationToken ct = default);
IAsyncEnumerable<SessionObservationSnapshot> WatchSessionAsync(SessionId id, CancellationToken ct = default);
SessionObservationSnapshot? ReadObservation(SessionId id);
IReadOnlyDictionary<SessionId, SessionObservationSnapshot> Observations { get; }
IReadOnlySet<SessionId> DeletedSessions { get; }
Task InterruptSessionAsync(SessionId id, CancellationToken ct = default);
```

The snapshot contains the requested `SessionId`, actual nullable `SessionInfo`,
typed `Messages`, unconsumed `Inbox`, direct Session `Permissions`, `Running`,
`Busy`, `Revision`, and `Deleted`. `Error` means observation/transport failure;
`ExecutionError` is the previous admission/execution failure and must not by itself
prevent a new command. No fake Session metadata or prompt identity is needed to
observe commands, synthetic input, or an already-running Session.

Observe hydrates and retains the shared Session entry. Refresh serializes a new
read for that entry. Watch attaches a bounded latest-snapshot reader to that entry;
it does not subscribe to SSE again. Reader cancellation removes only that reader.
The entry remains live through tab changes until adapter disposal.

Commands use their actual command HTTP operation/response, with observation started
before admission, then refresh/watch this same Session. The existing command partial
can share `Observation.Admission` for send ordering; it must not call ordinary prompt
admission or invent a user inbox ID for a command result.

## Projection and hydration

The shared receiver routes by event Session ID. Step, text, reasoning, retry and tool
events update their actual assistant/message identities immediately. Inbox events
update real IDs, payloads and delivery modes; promotion materializes a visible user
or synthetic message only from its actual admitted payload and delivery timestamp.
Other message domains refresh their real typed projected records. Session-created
and step-started events can establish offscreen read models, independent of a local
foreground submission.

Each Session revalidates serially. Reads load inbox before projected messages, apply
newer event changes, and keep consumed inbox IDs out of pending. Lifecycle events
received during active-state hydration supersede that read. A lookup response for
a different Session ID is rejected. Only a missing Session lookup (or a delete event)
marks the Session deleted; a missing secondary endpoint does not.

The public API cannot recover live-only deltas emitted before connection. Initial
hydration uses the server's projected content; subsequent deltas are live, and full
text/reasoning end events replace partial values. This is not a replay/exactly-once
claim for ephemeral events. A disconnected receiver reports typed stale/reconnecting
state without declaring the Session idle or interrupting it. The same receiver now
reconnects and hydrates behind a new-epoch event barrier; see `MULTI-ADMISSION.md`.

## Typed ordinary prompts

The adapter supports both canonical input types:

```csharp
PromptAsync(SessionId? origin, PromptInput input, CancellationToken ct = default);
PromptAsync(SessionId? origin, SessionPromptInput input, CancellationToken ct = default);
```

The Protocol `SessionPromptInput` overload preserves the caller's ID, URI/data-URI
files, names/descriptions/mentions, agents, skills, metadata, delivery and resume.
Collections and JSON metadata are captured before awaiting anything. The Schema
`PromptInput` overload preserves all fields available in that contract, which has
no metadata/delivery/resume properties. Neither overload fetches local media or
rewrites a captured URI.

The origin Session and creation configuration are captured before asynchronous
preparation. A newly created Session retains its client-selected ID across an
uncertain creation; opening that origin waits for creation rather than fetching it
prematurely. Each Session has one admission semaphore, shared with commands, while
different Sessions can admit concurrently. The semaphore is released after the
POST settles, not after model execution.

An unconfirmed POST retains its complete input and ID. A retry with the same complete
input reuses that ID; an explicit retry of that ID retains the original payload.
Positive enqueue/delivery/read facts win over an HTTP failure. Each item's uncertainty
survives independently when later sends proceed after its POST settles. Confirmation
or failure of a later input cannot clear the older record. Confirmed rejection remains
distinct from unknown transport/5xx/cancellation outcomes.
No retry mints a replacement ID for an unconfirmed admission.

The compatibility prompt response stream monitors its own input ID. For recovered
delivery it may finish as idle without inventing a per-input execution outcome when
the terminal event was missed. The shared Session read model remains authoritative
for the visible transcript and Session status.

## Root integration

`StreamAsync` and request-lifetime members now live in `OpenCodeApp.Streaming.cs`.
The root's existing `_request` getter is view-specific; `_stream` joins all owned
origin tasks at shutdown. Each update validates and routes to its origin. Background
updates never replace the selected Session, composer model/agent, response list,
error, or draft. Failed admission restores only an empty origin editor, including
attachments and metadata. Closing a new view does not resurrect it on late admission.

The production tab frame calls `ReadObservedSession`, and tab restoration reads the
latest observer before rendering. Transcript scroll/edit state remains in the
existing `_tabViews`; no duplicate tab state is created. Root tab metadata now
prefers observer watermarks over stale picker/readiness snapshots.

Bind:

```csharp
ReadSessionObservation = id => adapter.ReadObservation(id);
InterruptObservedSession = (id, ct) => adapter.InterruptSessionAsync(id, ct);
NetworkPromptInput = (id, input, ct) => adapter.PromptAsync(id, input, ct);
```

Ordinary submit must capture `CapturePromptAdmission(_input)` **before** clearing
the editor and attachments, then call `StreamAsync(captured, token)`. The legacy
string callback is used only for truly text-only input; attachment/metadata-bearing
input fails visibly if typed transport is missing. `RestoreProjectedUserPrompt`
must call `RememberPromptMetadata(tab, message.Metadata)` because the shared
unprepared `PromptInput` does not have a metadata member.

Use `SelectedSessionRunning` for running UI and `InterruptActiveSession()` for the
explicit interrupt command. Cancelling `_request` is now only local observation
cancellation. Keep host readiness presentations keyed by Session or selected view;
a background readiness callback must not overwrite another view's catalog/sidebar.

## Disposal and verification

Adapter disposal cancels/joins its SSE receiver, shared refreshes, outstanding HTTP
waits and origin submissions. It never calls session.interrupt. Only the explicit
`InterruptSessionAsync` user action does that. A cancelled reader or UI shutdown
cannot implicitly stop server execution.

Verification is pinned .NET 11 isolated CLI compilation only. No SSE subscription,
HTTP request, application, database, provider, process adapter, screenshot or test
was executed during implementation.
