# Question and subagent execution

## Registration handoff

The two real leaves are available, but this pass does **not** edit
`ToolLocationFactory` or its registration order:

```csharp
new QuestionTool(locationForms, locationPermissions).Create()
new SubagentTool(hostSubagents, locationPermissions).Create()
```

`locationForms` must be the actual `FormService` used by Form HTTP routes and MCP
elicitation for that Location. `locationPermissions` must be the same loaded
`PermissionService` used by the request snapshot and HTTP permission replies.
Neither leaf creates a registry or grants authority through catalog visibility.
Both register with `CodeMode: false`, as in source.

Construct one host-scoped service with:

```csharp
new SessionSubagents(database, sessionStore, sessionQueries, executionEngine, hostLifetime, sharedJobRuntime)
```

The lifetime must be cancellable. Use the same database/store/engine as the parent
Session and the existing ToolLocationFactory/PermissionLocationMap. Resolve the
service lazily in the factory's existing Location options callback if needed to
avoid constructor cycles; do not construct a second engine or permission map.

Subagents now use the same generic JobRuntime as shell work. The private run map and
specialized marker writer are removed; see `../BACKGROUND.md` for all-job promotion
and generation-safe completion notification details.
The SDK accepts this service through `subagents:` and exposes `client.Subagents`.
Injected services remain host-owned; the text-only/default-tool-less SDK constructor
does not silently install another tool graph.

The host owns engine shutdown and must settle execution before disposing stores,
Location permissions or forms. Dispose SessionSubagents to cancel/join its job and
notification tasks. Advisory execution is owned by the shared engine: cancelling
a job's resume joiner must not falsely claim that another execution owner stopped.

## Question

Source: `core/tool/plugin/question.ts`, `schema/question.ts`, and the existing
native `Forms/FormService.cs` implementation of `core/form.ts`.

- The real input/output codecs validate a nonempty questions array, question/header
  strings, option label/description strings and optional multiple selection.
  Source annotations such as a recommended header length are not invented hard limits.
- The leaf asserts `question` / `*` with the invocation's Session/agent/message/call
  identity before creating the form. Permission declines escape as control flow;
  blocked/corrected permission failures remain ordinary declared tool failures.
- Questions become `q0`, `q1`, etc. String/multiselect fields preserve option order,
  descriptions and headers, and use `custom: true`. No free-answer option, default,
  required answer, or automatic answer is fabricated.
- Form metadata is `{ kind: "question", tool: { messageID, id } }`. The actual shared
  FormService publishes creation/reply/cancellation and owns the pending waiter.
- Answer arrays and model text follow source formatting, including `Unanswered`
  only for genuinely missing/empty answer arrays after a real answered form.
- A dismissed form raises `QuestionCancelledException`. Session settlement records
  aborted tool/step facts and user interruption rather than returning an answer or
  starting another model step. Host/token cancellation remains ordinary interruption.

`SessionFailure` now follows source `to-session-error.ts` for ToolFailure causes:
unwrap a nonempty cause message, preserving permission correction feedback and
structured status; retain the curated tool message when the cause message is empty.
No leaf catches and converts question dismissal or permission decline into success.

## Child execution

There is no `core/session/subagent.ts` in this checkout. The implemented path maps
`core/tool/plugin/subagent.ts`, `core/job.ts`, `core/session/subagent-completion.ts`,
and Session creation/admission/execution domains.

- Count actual parent ancestry and apply `experimental.subagent_depth` (default 1).
  Resolve the requested agent from the real catalog, reject primary-only agents,
  and assert `subagent` permission for that agent before child creation.
- A supplied Session ID must name an existing child of this exact parent. Switching
  an existing child's agent uses canonical agent/model selection events. A configured
  agent model wins; otherwise a new child inherits the parent's model selection.
  Selection does not preflight credentials/provider availability before admission.
- New children have durable Session identity, parent ID, placement/project binding,
  and inherited parent metadata. They have fresh conversation/instruction-entry
  context, not a fork or copied parent transcript. Agent policies are resolved by
  the existing child runner and shared permission Location, not replaced with allows.
- Running progress exposes the actual child ID before prompt execution. New children
  receive the source subagent preamble. Prompt preparation/admission/wake and explicit
  run/join all use the shared SessionExecutionEngine and its one-physical-attempt path.
- The request's captured subagent description gains the source available-agent list,
  filtered by selected-agent permissions, mode and visibility. This only changes
  descriptions; it does not grant execution authority or alter captured tool identity.
- Foreground waits for actual execution settlement, then reads the latest successful
  completed assistant within the source's descending 20-message window. No-text
  fallback applies only after successful real execution. Failures/cancellation include
  the child Session ID and do not masquerade as empty completed work.

Children use the same Location services. Their form/permission events retain the
**child** Session ID and canonical tool source, not a relabelled parent ID. Parent UI
consumers can follow the durable parent/child relationship and tool progress link;
the Location-wide form/permission request surfaces include these actual requests.
No second approval/form registry or parent auto-answer behavior is installed.

## Background and cancellation

One process-local job generation per child joins concurrent resumes; subsequent
settled continuations get a new generation. A running child is still steered through
durable prompt admission before joining its job. There is no cached chat transcript,
provider tool loop, periodic poll, sleep or fabricated completion.

`background: true` returns source's running result and instructions not to poll.
`BackgroundAsync(parentId, childId)` can release an existing foreground waiter.
`CancelAsync(parentId, childId)` targets a known child of that parent. Foreground
parent cancellation interrupts the child through SessionExecution and waits through
cleanup; detached work is not cancelled just because the parent step ends. Host
shutdown cancels host-owned work using shutdown semantics rather than releasing
recovery claims as a pretend user cancellation.

The source-compatible `job.background/<notificationID>` KV marker contains only
child job identity, stable notification ID, recovery description/agent/parent/child,
and actual status/output/error. It is not clustered ownership or an execution claim.
Writes use the existing serialized transaction boundary; no new job event family
is invented. Each generation's observer holds that generation, not a later job
found by the same child ID.

Completion admits the exact source synthetic wrapper and metadata using the stable
notification ID. It wakes a parent only when no revert is staged, then removes the
marker after successful admission/wake. Synthetic completion admission is allowed
while a revert is staged; unlike new user prompts, it does not commit the revert.
Clear/commit retain their existing source inbox semantics. Delivery/persistence
failures leave markers and are observed/logged, not reported as success.

## Explicit remaining scope

- ToolLocationFactory and Server/SDK host registration remain the owner handoff above.
- Generic Job HTTP APIs/background-all UI operations and complete plugin runtime hooks
  are not implemented by this specialized service.
- Child-claim and background notification recovery is now implemented through the
  explicit combined startup entrypoint; see `RECOVERY.md`. It is never invoked by
  tool execution or construction. Unsupported shell/plugin domains and controls
  retain guards, and no persisted running marker becomes a fake completed result.
- This is process-local execution, not a clustered lease or exactly-once guarantee.
  A crash can occur after a side effect and before its durable result.
- Runtime behavior was not exercised. Verification is pinned .NET 11 compilation
  under `C:\tmp\opencode\core-finish-pass` and static source checks only. No tests,
  forms, Sessions, models, tools, processes, databases or filesystem operations were
  invoked as application verification.
