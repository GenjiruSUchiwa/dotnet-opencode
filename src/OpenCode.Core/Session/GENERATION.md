# Transient Session generation

Public Core and SDK APIs:

```csharp
Task<string> SessionExecutionEngine.GenerateAsync(SessionId sessionId, string prompt, CancellationToken ct = default);
Task<string> OpenCodeClient.GenerateAsync(SessionId sessionId, string prompt, CancellationToken ct = default);
```

The source protocol is POST `/api/session/:sessionID/generate`, payload
`{ prompt: string }`, response `{ data: { text: string } }`. It has no model override,
structured-output schema, resume flag, tool-execution option, or streaming response.
The Server owner can mount these methods using its existing result wrapper. Missing
Sessions use `SessionMutationNotFoundException`; other generation failures map to
the source service-unavailable response with service `session generation`.

## Selection and preview

Sources: `session/generate-node.ts`, `session/context.ts`, `session/history.ts`, and
`session/instruction-state.ts:preview`.

The operation acquires the same authoritative Location tool/permission composition
as the runner, checks placement after acquisition, observes MCP, resolves the selected
agent, and captures its real tool definitions and Code Mode catalog. Configured
plugin producers remain explicitly unsupported rather than being treated as empty
successful hooks. Model selection uses the Session's stored model/default catalog
selection; title small-model fallback and transient prompt overrides are not used.

Observation is separated from instruction admission. `ObserveInstructionsAsync`
does not initialize or commit an epoch. `InstructionPersistence.PreviewAsync` reads
instruction state and context in one SELECT-only transaction:

- With no epoch, it renders the observed available values as a temporary baseline.
  Unavailable initial sources block generation; no blobs/hash maps are inserted.
- With an epoch, it renders the stored initial baseline and computes an uncommitted
  update against stored current values. Unavailable observations retain prior values.
- History begins at the latest completed compaction, inclusive. Every selected row
  is decoded, then only the prefix before the first incomplete assistant is included.
  Unresolved active tool work is not settled, fabricated, or added to the request.

Normal durable instruction preparation and preview share the same delta/rendering
implementation. The normal runner's write path remains separate and unchanged in
purpose. Generate does not update instruction_state/instruction_blob, enqueue or
deliver inbox items, append messages, claim execution, wake a drain, rename a title,
or publish usage/step events.

## Request and response

The real system prompt and preview baseline precede the selected history. Any
uncommitted instruction update is a chronological System message, followed by the
caller's transient user prompt. Existing history/media lowering and authoritative
model modality fallback/image bounds apply. Canonical Session/project/parent headers
and the source Session/fork prompt-cache lineage are supplied; configured supported
agent headers/body remain in force.

Like source LLM.generate, the native boundary collects exactly one structured
StreamAsync call. Tool definitions remain advertised, but no returned tool call is
dispatched, no Code Mode program is run, and no follow-up model loop starts. The
operation has no title fallback or Session retry policy.

Text deltas are joined per fragment, with authoritative TextEnd replacing that
fragment's previous text. Final output preserves fragment order and is not trimmed,
reworded, or replaced with a placeholder. A terminal finish or emitted provider-error
completes the source response projection; an emitted error can therefore yield only
the actual partial/empty text. Thrown provider/transport errors propagate, and a
stream lacking a terminal response raises incomplete-output failure. Non-text
response parts are not exposed by this text-only protocol and are never executed.

Usage is diagnostic only, using observed token fields. It is not added to Session
cost/tokens. Caller cancellation propagates through selection and the single stream,
and the Location lease is released on success, failure and cancellation. No background
task, execution coordinator registration or durable generation job is created.

## Remaining parent-owned contracts

- Generic message ID/metadata and content metadata are now supplied by the LLM
  contracts and preserved by SessionHistory. User/agent attachments, shell metadata
  and media descriptions no longer hit the former representation guards; see
  `HISTORY-METADATA.md`. No annotation is converted into invented prompt text.
- SessionRequestIdentity now accepts actual host client/user-agent values. The owned
  SDK supplies its explicit client name and real application build fingerprint; other
  hosts can inject their identity without guessing a product version. Known Session
  identity headers and prompt-cache lineage remain implemented.
- Explicit workspaces, unsupported provider/request settings and plugin hooks remain
  explicit boundaries. Schema/protocol structured generation options are not invented.
- Read-only means Session persistence is unchanged. Source selection can refresh
  process-local catalogs/MCP state and reads local producers; it is not a promise that
  provider generation or Location acquisition has no external effects when invoked.

Verification is isolated pinned .NET 11 SDK/Core/Schema compilation and static
checks only. No tests, generation requests, runtime database/filesystem operations,
models, tools, Git, API calls, network calls or process control were executed.
