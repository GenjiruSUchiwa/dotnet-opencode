# Session HTTP Client

`SessionHttpClient` is a network-only client. It references Schema/Protocol, never
Core or Server. It does not discover/start a daemon, open a database, execute a
model, or own a session worker. Supply a `ServiceEndpoint` obtained from the
service-discovery layer or an explicitly selected server.

Permission request/list/reply and saved-permission APIs share this client. See
[PERMISSION-HTTP.md](PERMISSION-HTTP.md) for dialog integration and exact contracts.
Agent/model/provider picker methods are documented in [CATALOG-HTTP.md](CATALOG-HTTP.md).

## Integration API

```csharp
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

using var client = new SessionHttpClient(endpoint);
var session = await client.CreateAsync(
    new SessionCreateInput(Location: new LocationRef(projectDirectory)), cancellationToken);
var messageId = MessageId.Create();
var admission = await client.PromptAsync(session.Data.Id,
    new SessionPromptInput("Hello", Id: messageId), cancellationToken);
```

This snippet only illustrates admission. The UI must establish its event stream
and observe `server.connected` before submitting prompts it wants to observe,
then keep consuming that stream independently in its own lifecycle. The client
does not add a background pump or concurrent orchestration worker.

| Method | Result |
| --- | --- |
| `CreateAsync(SessionCreateInput, ct)` | `ApiResult<SessionInfo>` |
| `GetAsync(SessionId, ct)` | `ApiResult<SessionInfo>` |
| `ListAsync(SessionListQuery?, ct)` | `ApiPage<SessionInfo>` containing `Data` and `Cursor` |
| `MessagesAsync(SessionId, SessionMessagesQuery?, ct)` | `ApiPage<SessionMessage>` |
| `InboxAsync(SessionId, ct)` | `ApiResult<IReadOnlyList<SessionInboxItem>>` |
| `PromptAsync(SessionId, SessionPromptInput, ct)` | `ApiResult<SessionInboxItem>`, verified as the submitted user admission |
| `InterruptAsync(SessionId, bool? continueExecution, ct)` | `InterruptSessionResponse` |
| `ActiveAsync(ct)` | `ApiResult<IReadOnlyDictionary<string, SessionActive>>` |
| `ViewAsync(SessionId, double idle, ct)` | `Task`, completes only on HTTP 204 |
| `SetEnvironmentAsync(SessionId, IReadOnlyDictionary<string, string>, ct)` | `Task`, completes only on HTTP 204 |
| `RenameAsync(SessionId, string title, ct)` | `Task`, completes only on HTTP 204 |
| `SwitchAgentAsync(SessionId, AgentId, ct)` | `Task`, completes only on HTTP 204 |
| `SwitchModelAsync(SessionId, ModelRef, ct)` | `Task`, completes only on HTTP 204 |
| `DeleteAsync(SessionId, ct)` | `Task`, completes only on HTTP 204 |
| `SubscribeEventsAsync(ct)` | `IAsyncEnumerable<ServerEventEnvelope>` |

Prompt attachments use `PromptInputFileAttachment` URI inputs and
`PromptInputSkillAttachment` IDs, not prepared base64 prompt attachments.
`delivery` and `resume` are omitted when unspecified. Model/agent selection belongs
to session configuration, not extra fields on the canonical prompt request.

There are no automatic request retries. If an admission result is lost, callers
must retain the original message ID before deliberately retrying; do not invent
a new ID for the same admission. Cancellation of an HTTP call or event stream
does not interrupt durable server execution. Use `InterruptAsync` explicitly.

## Session Mutations

These methods expose the production protocol, not a claim that every native
server currently implements every operation. Unsupported backend responses remain
errors, including active-list or deletion unavailability. There is no fallback to
embedded Core, no inferred active set, and no local mutation.

| Client method | Exact request | Success | Declared errors |
| --- | --- | --- | --- |
| `ActiveAsync` | GET `/api/session/active`, no payload or query | 200 `{data: {sessionID: {type: "running"}}}` | Auth/schema middleware 401/400; no endpoint-specific error declaration. |
| `ViewAsync` | POST `/api/session/:sessionID/view`, `{idle: number}` | 204, no body | SessionNotFoundError 404; middleware 400/401. |
| `SetEnvironmentAsync` | PUT `/api/session/:sessionID/environment`, `{variables: Record<string,string>}` | 204, no body | SessionNotFoundError 404; middleware 400/401. |
| `RenameAsync` | POST `/api/session/:sessionID/rename`, `{title: string}` | 204, no body | SessionNotFoundError 404; middleware 400/401. |
| `SwitchAgentAsync` | POST `/api/session/:sessionID/agent`, `{agent: string}` | 204, no body | SessionNotFoundError 404; middleware 400/401. |
| `SwitchModelAsync` | POST `/api/session/:sessionID/model`, `{model: Model.Ref}` | 204, no body | SessionNotFoundError 404; middleware 400/401. |
| `DeleteAsync` | DELETE `/api/session/:sessionID`, no payload or query | 204, no body | SessionNotFoundError 404; middleware 400/401. |

`idle` is the observed idle transition's epoch-millisecond value, not a generated
current timestamp. It uses the canonical nonnegative integer-number contract,
including integer-valued numeric encodings and no invented Int32/Int64 ceiling.
The server decides whether the observed transition still needs to be marked.

Environment replacement is whole-map replacement, not merging. `{}` remains an
explicit empty override. The client does not modify its process environment.
Rename preserves the supplied title; an empty string requests upstream automatic
title generation and is not replaced by a client-generated title.

Agent IDs and Model.Ref values are sent unchanged. Schema owns identifier and
model-reference encoding; the client does not infer a provider, select a model,
normalize a variant, or run discovery while switching selection. Server catalog
and capability validation remain authoritative.

Active-map keys remain JSON object strings for direct lookup with
`sessionId.Value`, and each is validated through Schema's SessionId constructor.
Values must be the canonical `running` variant. Missing entries do not imply a
completed outcome, and queued sessions are not synthesized into the active set.

All mutations dispose requests/responses on completion, error, and cancellation.
A 204 response requires no JSON content type or JSON decoding. A different status,
including an unexpected 200, is not treated as success; SessionApiException
retains both actual and expected status plus the returned payload. Cancellation
after sending can leave a mutation's outcome uncertain. No mutation is
automatically retried, including PUT, DELETE, or repeated view requests; callers
must reconcile server state before deciding how to proceed.

## Queries And Cursors

Canonical HTTP DTOs, response envelopes, and query options now live in
`OpenCode.Protocol.Groups`. Their shared generated metadata is
`OpenCode.Protocol.SessionProtocolJsonContext`. Client no longer declares shadow
copies. Add the Protocol namespace import when updating existing integrations;
the interrupt result now uses the existing `InterruptSessionResponse` name.

`SessionListQuery.All()`, `ForDirectory(directory)`, and
`ForProject(project, subpath)` construct the three upstream query modes. Use
record `with` expressions to add workspace, search, order, limit, or parent
filters. `RootOnly` emits the literal query value `parentID=null`; omitting both
parent options means no parent filter. Workspace is independent of the directory
or project mode. Source subpath matching applies only when project is present.

The public upstream HTTP query is flat and accepts directory/project field
combinations. This client forwards them rather than inventing rejection rules
or path normalization for the server's operating system. Use a single named mode
for ordinary pagination: the server's cursor stores a mode-specific query.

`SessionListQuery.FromCursor(cursor, limit)` forwards an opaque session cursor.
Upstream replaces supplied filters/order with the cursor's stored query, while
the separately supplied limit still applies. Even an empty session cursor is
forwarded: upstream treats its presence as a decode request and returns its own
`InvalidCursorError`. No client-side base64/JSON decoding occurs.

`SessionMessagesQuery.FromCursor(cursor, limit)` likewise forwards an opaque
message cursor. A nonempty message cursor combined with order raises local
`SessionQueryValidationException` carrying `InvalidCursorError` and the exact
message `Cursor cannot be combined with order`. An empty message cursor does not
conflict with order, matching the upstream handler's truthiness check. Message
limits are 1..200. Session-list limits are positive finite integers, with no
invented Int32 ceiling; omitted limits/order retain server defaults.

The native backend may explicitly reject filters it has not implemented. The
client does not suppress those options, retry with fewer filters, decode the
cursor, or fabricate an empty result.

## Events

Events preserve their full JSON in `Raw` and expose unmodified object payloads as
`Data: JsonElement`. Consumers can decode a supported event payload with the
appropriate Schema source-generated metadata. Unknown payload fields and event
types are not discarded or converted into guessed text deltas.

Ordinary events decode through the canonical Schema `OpenCodeEvent` metadata,
including its location, timestamp, ID, and durable-number validation. The Client
keeps only the protocol-specific connected variant because `server.connected`
can omit `created`; no creation time is fabricated. The public envelope is a
stable view over the decoded fields and complete raw JSON. It does not validate
every event type's domain-specific payload or infer durability from an incomplete
native manifest. No Schema file is modified by this client.

The SSE reader handles UTF-8/BOM, split CRLF, CR/LF lines, comments/heartbeats,
multiline `data` fields, and the generated Promise client's final unterminated
data block behavior. Event buffering is bounded to 16 MiB of characters, matching
the Promise transport's string-length guard. There is no automatic reconnect,
`Last-Event-ID` replay, or implicit reconciliation: `/api/event` is volatile.
After disconnection, the UI must reconcile through read APIs before continuing.

## Errors And Ownership

`SessionApiException` retains HTTP status, raw response body, and parsed JSON
payload when available, including undeclared server errors. Its message does not
include prompt or response contents. `SessionProtocolException` identifies an
unsupported content type, malformed response, unavailable schema support, or an
oversized event. Bare arrays and missing/null required envelopes are rejected;
missing fields are not replaced with fabricated sessions or inbox records.

For declared query errors, `SessionApiException.QueryError` also exposes the
canonical tagged `InvalidRequestError`, `InvalidCursorError`,
`SessionNotFoundError`, `UnauthorizedError`, or `UnknownError`. Original `_tag`,
message, kind/field/ref, extra properties, and raw JSON remain available. Unknown
or malformed error variants retain the original HTTP status/body instead of
being coerced into a known error. Local query validation is separately reported
as `SessionQueryValidationException`; it does not claim a request was sent.

The client uses explicit source-generated JSON metadata. Schema-owned ID,
timestamp, tagged-union, and optional-field converters remain authoritative.
Unknown message/inbox variants that those converters cannot represent fail
explicitly rather than being coerced into another variant.

Requests apply endpoint authentication individually. The default HTTP handler
does not follow redirects. An injected `HttpClient` remains caller-owned; it must
be configured without automatic mutation retries or credential-forwarding
redirects. Disposing `SessionHttpClient` cancels its outstanding requests and
streams but does not dispose an injected HTTP client. Breaking or cancelling an
event enumeration disposes its reader, response, and request. Error responses
are also disposed. No worker survives an enumeration.

## Source Mapping

- `packages/protocol/src/groups/session.ts`: create/get/list/prompt/inbox/interrupt.
- `packages/protocol/src/groups/session.ts:220-257,276-321,730-755`: active,
  removal, selection, rename, environment replacement, and view contracts.
- `packages/server/src/handlers/session.ts:185-207,233-262`: mutation delegation,
  no-content completion, and empty-title automatic generation behavior.
- `packages/protocol/src/groups/message.ts`: message query and cursor envelope.
- `packages/protocol/src/groups/event.ts`: volatile SSE contract.
- `packages/client/src/promise/generated/client.ts:264-395,2136-2180`: transport,
  query encoding, status/content-type handling, SSE framing, and disposal.
- `packages/server/src/handlers/event.ts`: connected frame and heartbeat shape.

Server execution/durability gaps remain server responsibilities. A compiled
network client does not establish that the current native server faithfully
implements admission, interruption, projection, or streaming semantics.
