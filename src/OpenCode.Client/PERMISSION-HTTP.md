# Permission HTTP Client

Permission operations are methods on `SessionHttpClient`, sharing its existing
HTTP transport, endpoint authentication, cancellation lifetime, error handling,
and 204-response disposal. No second client, worker, retry policy, or Core/Server
dependency is introduced.

## Dialog Integration

Import `OpenCode.Client`, `OpenCode.Protocol.Groups`, and `OpenCode.Schema`.
Use the selected session's ID, and preserve the permission request's own ID:

```csharp
var pending = await client.ListSessionPermissionsAsync(sessionId, cancellationToken);
var owned = await client.GetPermissionAsync(sessionId, requestId, cancellationToken);
await client.ReplyPermissionAsync(sessionId, owned.Data.Id,
    PermissionReply.Once, ct: cancellationToken);
```

The dialog must send the user's selected reply. Neither the model nor a client
fallback should choose approval. `Once`, `Always`, and `Reject` encode exactly as
`once`, `always`, and `reject`; integer JSON values are not accepted. Optional
feedback is the `message` property, not a renamed `feedback` field. Null means
omit it, while an explicitly supplied empty string is retained. The client does
not restrict feedback to a particular reply beyond the source contract.

`permission.asked` event data can be decoded with
`OpenCode.Protocol.PermissionProtocolJsonContext.Default.PermissionRequest`.
Keep the UI's event subscription alive and reconcile pending requests after a
volatile-stream disconnect. A stale request may disappear before a reply; handle
the actual not-found response, not an assumed successful approval.

### PermissionComposer Callback

The existing UI-owned `PermissionDecision` carries `SessionId`, `RequestId`,
`Reply`, and `Feedback`. Its `OnReply` parameter can be wired in UI code directly:

```csharp
Func<PermissionDecision, CancellationToken, Task> onReply = (decision, token) =>
    client.ReplyPermissionAsync(decision.SessionId, decision.RequestId,
        decision.Reply, message: decision.Feedback, ct: token);
```

Here `PermissionDecision` is the composer's UI type, not Protocol's
`PermissionDecisionInfo` evaluation result. The Client references neither the
composer nor Core. No extra transport adapter or reply DTO is needed in the UI.

The dialog's session-scoped ListPending operation is
`ListSessionPermissionsAsync`; use its `.Data` array. For a Location-wide dialog,
use `ListPermissionRequestsAsync` and retain both `.Location` and `.Data`.
`GetPermissionAsync` returns the actual `.Data` request; `CreatePermissionAsync`
returns only the actual `.Data` decision. None of these APIs silently unwraps
the response envelope or constructs requests/grants from a convenience result.

Pass the authoritative request to `PermissionComposer.Request`. Keep
`CanPersistAlways` false unless a trusted server/Location capability explicitly
establishes durable grant support. Neither nonempty `Save`, an `Always` enum
member, nor a successful pending-list call proves persistence. The supplied
canonical permission read APIs do not themselves expose that capability. The native
host extension `PermissionCapabilitiesAsync()` now reports
`Data.PersistentGrants` from the actual shared durable-store registration. Only
use a successful true response to enable the persistence affordance; a 404 from
another server is not evidence of support.

Await the reply callback before presenting it as sent. On 404, reconcile the
pending list; do not claim the user's reply was accepted. On unavailable/error,
keep the error visible without choosing another reply. Cancellation may leave
delivery uncertain, so refresh authoritative state before another user action.

## API Surface

| Method | Wire request | Result / success | Endpoint errors |
| --- | --- | --- | --- |
| `CreatePermissionAsync(sessionId, PermissionCreateInput, ct)` | POST `/api/session/:sessionID/permission` | 200 `ApiResult<PermissionDecisionInfo>` | SessionNotFoundError 404. |
| `ListPermissionRequestsAsync(directory?, workspace?, ct)` | GET `/api/permission/request` | 200 `LocationResponse<IReadOnlyList<PermissionRequest>>` | No endpoint-specific declaration. |
| `ListSessionPermissionsAsync(sessionId, ct)` | GET `/api/session/:sessionID/permission` | 200 `ApiResult<IReadOnlyList<PermissionRequest>>` | SessionNotFoundError 404. |
| `GetPermissionAsync(sessionId, requestId, ct)` | GET `/api/session/:sessionID/permission/:requestID` | 200 `ApiResult<PermissionRequest>` | SessionNotFoundError / PermissionNotFoundError 404. |
| `ReplyPermissionAsync(sessionId, requestId, reply, message?, ct)` | POST `/api/session/:sessionID/permission/:requestID/reply` | 204, no body | SessionNotFoundError / PermissionNotFoundError 404. |
| `ListSavedPermissionsAsync(projectId?, ct)` | GET `/api/permission/saved` | 200 `PermissionSavedListResponse` | No endpoint-specific declaration. |
| `RemoveSavedPermissionAsync(id, ct)` | DELETE `/api/permission/saved/:id` | 204, no body | No endpoint-specific declaration. |

Shared schema/auth middleware can return 400/401. Every unexpected status,
including current native 503 unavailable responses, is retained in
`SessionApiException` with its body and parsed payload. PermissionNotFoundError
is additionally decoded into the existing typed `QueryError` union, retaining
its exact `requestID` and message. No mutation reports success on an unexpected
200 instead of the declared 204.

## Exact Fields

`PermissionCreateInput` requires `action` and `resources`. Optional fields are
`id`, `save`, `metadata`, `source`, and `agent`. Session identity is in the route,
not duplicated into the create body. The response decision contains only `id`
and `effect` (`allow`, `deny`, or `ask`); allow/deny need not create a pending
request. Do not manufacture a pending request from the submitted fields when
the server returns a decision. Fetch the authoritative request or consume its
asked event when approval is required.

Canonical `PermissionRequest` contains `id`, `sessionID`, `action`, `resources`,
and optional `save`, `metadata`, `source`, and `message`. It does not contain an
agent field. Tool source is exactly:

```json
{"type":"tool","messageID":"message identity","id":"tool caller identity"}
```

The source's `id` is the caller/tool-call identifier. It is not the permission
request ID and is not renamed to a top-level `callerID`. Source identifiers are
plain strings in this contract; the client does not impose message-ID prefixes
on them. Metadata uses `JsonElement` values so opaque caller metadata is retained.
Empty resource/save arrays and empty action strings are not prohibited by the
public schema; null values are not silently converted to empty values.

Permission IDs accept the upstream legacy `per` prefix, not only `per_`.
Session IDs use Schema's legacy-compatible validator. Saved permission IDs are
an unrestricted string brand; no `psv_` requirement is added by the client.

Location listing uses deep-object keys `location[directory]` and
`location[workspace]`. The latter is not `location[workspaceID]`, which belongs
to a different wire shape. Omitted fields retain server placement defaults.
Saved listing uses only the optional `projectID` query; omission leaves the
upstream current-Location project default in effect. No client-cwd or project
guess is substituted.

## Validation And Support Boundaries

Schema's PermissionRequest, PermissionSource, PermissionSavedInfo, IDs and enums
remain the canonical CLR types. Protocol adds only request/decision DTOs and
source-generated metadata. Its permission-scoped converters reject missing
required fields, explicit null optional fields, invalid source tags, non-string
resource entries, and null location envelopes where the current Schema codecs
do not yet enforce those rules. They delegate construction/encoding to shared
Schema metadata rather than defining shadow request models. If Schema cannot
represent an otherwise valid value, decoding fails explicitly; nothing is
fabricated to make the response usable. These adapters can be retired when the
Schema codecs enforce the same complete boundary.

The managed Server now registers one `SqlitePermissionGrantStore` against its
existing durable database and shares it for evaluation, saves, listing, and removal.
Saved listing defaults to the resolved request Location's project; explicit
`projectID` takes precedence. Optional named `directory`/`workspace` arguments on
`ListSavedPermissionsAsync` select the request Location without guessing client cwd.
An accepted `always` reply commits its save before approving the waiting tool.
Alternative memory-only hosts still retain the existing guard. No client downgrades
`always` to `once`, and storage failures never become successful approval.

All operations use caller cancellation and the owning client's disposal token.
Breaking an event enumeration or disposing the client releases its requests as
documented in [SESSION-HTTP.md](SESSION-HTTP.md). An injected HttpClient remains
caller-owned and must not add mutation retries. No permission operation is
automatically retried, even with a supplied ID. After uncertain delivery,
reconcile with list/get before the user deliberately decides the next action.

## Source Mapping

- `packages/protocol/src/groups/permission.ts`: all route, payload, response,
  status, and endpoint error declarations.
- `packages/protocol/src/groups/location.ts`: deep-object LocationQuery keys.
- `packages/schema/src/permission.ts`: Request, Source, Reply, Effect and ID.
- `packages/schema/src/permission-saved.ts`: saved record and unrestricted ID.
- `packages/schema/src/location.ts`: non-null Location.response envelope.
- `packages/server/src/handlers/permission.ts`: request ownership, authoritative
  unloaded-Location empty lists, evaluation decisions, replies and project defaults.
- `packages/client/src/promise/generated/client.ts`: encoded path/query keys,
  exact 200/204 transport behavior and cancellation.

No Server endpoint, Core permission service, saved-grant store, or UI dialog is
implemented or modified by this client addition.
