# Automatic and explicit title generation

`SessionTitleService(database, sessions, providers, hostLifetime)` is the real
auxiliary title service. The host lifetime must be cancellable. Pass the same
service to `SessionExecutionEngine(..., titles: service)`.

## Trigger and Server handoff

- The runner schedules automatic work only after input promotion, for a top-level
  Session whose title is missing or exactly its timestamped root fallback.
- The exact fallback is `New session - <created UTC ISO timestamp with milliseconds>`.
  An empty string, whitespace, arbitrary "New session" label, another timestamp,
  or a child fallback is not silently treated as missing.
- Scheduling is deferred and coalesced per Session while automatic generation is
  in flight. It does not delay durable admission or the primary model request.
  Admission-only `resume: false` does not promote input or start title generation.
- For the source empty-rename operation, call `GenerateAsync(sessionId, ct)` directly.
  Nonempty values, including whitespace, still use normal Session rename. Missing
  Sessions or no first user message produce no title request, as in the title service.

No Server endpoint was edited. The SDK exposes `Titles` and `GenerateTitleAsync`;
its owned constructor supplies the service and lifetime, and injected hosts pass
`titles:`. SDK AskAsync no longer fabricates a title by truncating the prompt.

## Source selection and prompt

Sources: `session/title.ts`, `session/context.ts:selectTitle`,
`catalog.ts:Catalog.model.small`, `util/session-title-fallback.ts`, and
`session/runner/llm.ts`.

The service reads the first projected user message across history, not pending
input or an unprepared URI payload. Initial generation uses its actual text.
Explicit regeneration of an existing title uses the source's bounded context:
first request up to 2,000 UTF-16 code units, followed by a tail of recent user and
assistant text from current context selection, with a total budget of 8,000 units.
Compaction context cutoffs remain authoritative. A message decode failure falls
back to the first request, not a fabricated short label.

The dedicated `title` agent supplies its system prompt and supported request
headers/body. Its configured model takes preference. Otherwise, the service chooses
an enabled active text-input/text-output model from the primary provider, ordered
by release time within these source family priorities:

1. `gpt-luna`
2. `gemini-flash-lite`
3. `gemini-flash`
4. `claude-haiku`

No family or cost is guessed from a model name. The configured title variant wins;
otherwise `none`, `minimal`, then `low` are considered. Unavailable/unsupported
catalog candidates and typed LLM resolution failures use the source primary-model
fallback. Native unsupported configuration errors are explicit rather than
silently discarding settings. If no model can be selected, no title is invented.

Title selection does not acquire tools, observe MCP, initialize instruction epochs,
or run ordinary prompt admission. Configured/discovered plugin hooks remain an
explicit unsupported boundary through AgentCatalog; they are not represented by
empty successful hook callbacks.

## Physical requests, output, and publication

Each attempt contains one explicit `StreamAsync` request with the title agent's
system prompt, one user message, and tools disabled. There is no tool loop or normal
Session retry/backoff. A failed/empty selected-model attempt may make exactly one
fallback attempt when the primary model reference is distinct.

The title is the first nonempty trimmed line of generated text deltas. Source
cleaning does not invent quote/backtick stripping. The generated result is truncated
to 100 UTF-16 code units using a 97-unit prefix plus `...` when needed. No prompt
substring is substituted for an absent model result.

Actual usage is published as `session.usage.recorded.1` with source `title`, using
selected catalog pricing. Available usage is settled even on interruption. No
assistant message, Step event, execution claim, or synthetic prompt is created.

Before renaming, the service samples the next aggregate sequence and reloads the
Session. It skips a missing Session, a title changed since generation began, or an
already matching result. The canonical rename transaction also checks that sampled
sequence. A concurrent event causes the rename to be skipped, matching source's
late sequence protection—not a speculative lock held across model generation.
`session.renamed.1` remains the sole title mutation event.

## Lifetime and limits

Automatic work is host-owned rather than tied to a caller's prompt enumeration.
Explicit generation observes caller cancellation plus host shutdown. Disposal
cancels and awaits all requests before the SDK closes its store/HTTP resources.
This is an in-flight task map, not a durable Job queue; no title recovery marker or
automatic model execution is fabricated after a restart.

Server host injection/empty-rename routing remain the owner handoff. Generic
instruction-aware `SessionGenerate` is a different source path and is not replaced
by this title service. Plugin model-request hooks, unsupported agent request settings,
workspace routing, and runtime/provider verification remain explicit limitations.

Verification: isolated repository .NET 11 SDK/Core/Schema compilation and static
checks only. No tests, model requests, database/runtime filesystem operations,
process control, Git operations or network calls were used as app verification.
