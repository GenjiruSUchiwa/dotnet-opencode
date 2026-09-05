# Location-owned Forms

Source: `packages/core/src/form.ts` (create/ask/get/list/state/reply/cancel and
validation), `packages/schema/src/form.ts` (existing native Schema contracts).

`FormService` is one typed pending/terminal registry per Location. It uses existing
`FormInfo`, `FormField`, `FormAnswer`, `FormState`, and `FormEventDefinitions`.
There is no database table, loose JSON registry, policy approval, or global form
lookup. Pending entries do not expire; answered/cancelled entries expire after ten
minutes. Lists return pending entries only, optionally filtered by owner.

Creation rejects duplicate live/retained IDs and invalid `when` references before
publication. It inserts the entry before `form.created`, rolling it back if
publication fails. Reply/cancel publish the canonical event before changing state
and completing the waiter. Concurrent mutations serialize on one service gate.
Already-settled and missing forms have distinct typed errors. Shutdown cancels
pending forms and settles waiters; it never manufactures answers.

`FormValidation` preserves source ordering and messages: unknown keys first,
external acknowledgment, all-of conditions, required/active fields, scalar types,
string bounds/patterns/formats, numeric bounds/integrality, and multiselect/custom
option bounds. Conditions reference only earlier input fields, use target-compatible
scalar values, and respect closed options. Unanswered references are false for
both eq and neq. Defaults are descriptors, not server-generated answers.

Fixed ECMAScript regex/date expressions use the already-referenced managed Jint
engine, with form values bound as data. URL checks use the existing AngleSharp URL
parser. No CLR exposure, guest code interpolation, process, or network adapter is
installed. These are managed library implementations, not a claim that every
engine-specific JavaScript date/regex corner case has been runtime-verified.

## MCP

`FormService` implements `IMcpElicitationForms` directly. `AskAsync(FormInfo, ct)`
preserves the supplied ID, global/session owner, fields, and metadata. A cancelled
ask cancels the form. `TryReplyAsync` returns false only for missing/settled forms;
invalid answers and other failures propagate. Both calls use the same registry as
HTTP. MCP owns form-vs-URL conversion and completion correlation; the Form service
only accepts an external field when its answer is boolean true.

`Server.FormLocationServices.ForLocation` is the pre-observation host factory.
`LocalToolOptions.McpForms` receives that same service. It does not reenter the
permission Location map while the factory creates a Location. HTTP obtains a
Location lease, while teardown invalidates its form service. The host's global
sentinel is still scoped to the caller's Location, not all projects.

## HTTP and Client

All seven routes follow `packages/protocol/src/groups/form.ts` and
`packages/server/src/handlers/form.ts`, including owner checks and 204 completion:

- GET `/api/form/request`
- GET/POST `/api/session/{sessionID}/form`
- GET `/api/session/{sessionID}/form/{formID}`
- GET `/api/session/{sessionID}/form/{formID}/state`
- POST `/api/session/{sessionID}/form/{formID}/reply`
- POST `/api/session/{sessionID}/form/{formID}/cancel`

`FormProtocolJsonContext` uses existing Schema DTOs plus shared `ApiResult<T>` and
`LocationResponse<T>`. Client methods are `ListFormRequestsAsync`, `ListFormsAsync`,
`CreateFormAsync`, `GetFormAsync`, `FormStateAsync`, `ReplyFormAsync`, and
`CancelFormAsync`. Owner is a string to preserve the temporary `global` sentinel.
Location directory/workspace options matter for that sentinel; real sessions use
their stored placement. Errors are retained by `SessionApiException`, not converted
to empty lists or success.

No tests, live requests, storage operations, or process/network operations were
performed for this implementation. Validation is isolated compilation only.
