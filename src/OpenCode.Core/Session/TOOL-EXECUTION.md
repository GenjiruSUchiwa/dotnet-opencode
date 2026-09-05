# Captured Tool Execution

The runner accepts the host's `ToolLocationFactory` and `PermissionLocationMap` as
optional constructor dependencies. They must be the same pair registered by Server's
`AddLocalToolLocations`, including its permission dispatcher and HTTP adapter. Core
does not create another map, install permissive defaults, or execute through the
legacy process-global registry. SDK embeddings without this composition remain
text-only; configured visible skills still require a real skill-loading capability.

Readiness acquires the Location and captures a policy-filtered snapshot before new
input is admitted. Each physical request acquires its own lease and captures the
exact definitions/executors it will use. The lease remains alive through provider
completion, permission waits, local tool tasks, and durable terminal settlement.
Factory mismatch, dispatcher failure, missing host dependencies, and unsupported
CodeMode catalogs fail explicitly. Body overlays cannot replace captured tool
definitions or tool choice.

## Step Ordering

- One `Client.StreamAsync` call per physical attempt. Input fragments are not executable.
- Canonical object-valued `ToolCall` commits `session.tool.called` before invoking
  the captured snapshot. Invocation context uses the actual Session, selected Agent,
  assistant Message and provider call IDs, never fields from tool input.
- Local tool tasks can run while the provider body is read. Provider completion
  publishes `session.step.streamed`, independently of tool completion.
- The runner awaits every owned call. Only `ToolExecutionException` becomes a
  recoverable model-visible tool failure. Decline, cancellation, permission blocks,
  codec/contract defects and infrastructure failures keep their control/failure
  semantics. User declines release execution claims as user interruption, not shutdown.
- Declared machine output is validated by `ToolSnapshot.ExecuteAsync`; the source
  `session.tool.success.2` event stores canonical content/metadata, not an invented
  machine-output field. Generic bounds run after snapshot execution/hooks. Progress
  is ephemeral; failures snapshot observed progress into terminal metadata.
- Step end/failure commits after call settlement. Local called or failed calls may
  request a new logical step. That step reloads projected results/instructions;
  it never appends an in-memory SDK tool conversation.
- Tool continuations promote steers only. Queued input stays parked until idle.
  New promoted input resets the selected agent's step allowance once per batch.
  Configured agent limits retain definitions, set tool choice to none, and add the
  exact upstream max-steps prompt on the final logical call.

## Read and Skill Settlement

The daemon must register the mandatory hook lazily:

```csharp
services.AddSingleton<LoadReadInstructions>(sp =>
    (session, paths, ct) => sp.GetRequiredService<SessionExecutionEngine>()
        .LoadReadInstructionsAsync(session, paths, ct));
```

`ReadInstructionLoader` implements `session/instructions.ts`: temporary in-flight
Session/path claims, deduplication only from model-visible synthetic metadata after
the latest completed compaction, safe file reads, and one direct `session.synthetic`
publication containing text and `metadata.instruction.paths`. It uses the executing
tool's already-loaded authoritative Location for project-relative descriptions.
The callback is a trusted host boundary, not an untrusted paths endpoint. Claims
are released on every normal/error/cancellation exit; no permanent path ledger,
inbox row, instruction epoch replacement, or API entry is manufactured. The operation
participates in the coordinator's deletion reservation accounting.

Skill loading instead uses the ordinary result path:
`snapshot.ExecuteAsync -> output bounds -> session.tool.success.2`. It never invokes
the read-instruction loader or creates a duplicate synthetic. Guidance is enabled
only if the surviving captured definitions contain the actual `skill` tool.

## Provider State

Hosted calls (`ProviderExecuted`) are never executed locally. Supported hosted
text/JSON/media results and structured failures retain call/result provider state.
Unexpected hosted or opaque output fails explicitly. Responses itemId/phase and
encrypted reasoning metadata survive full-context reconstruction; authoritative
end values replace buffers. responseId remains in the projected assistant's terminal
providerState. No unimplemented previous_response_id continuation is guessed.

Inline data-URI tool files lower to media without dropping bytes. Remote/managed
URIs require materialization. Some transports still reject media-bearing tool results;
that rejection is explicit, not a text-only fallback. Recovery classification remains
on the original LlmException; canonical errors retain source category/message/status,
not HTTP bodies/headers/URLs. No automatic retry, overflow rebuild or incremental
transport recovery was added.

## Output and Limits

`SessionToolOutput` follows `tool-output.ts`: honor producer truncation metadata,
otherwise use configured max_lines/max_bytes (defaults 2000 / 50 KiB), preserve file
parts, store complete text in the app tool-output directory, and publish the bounded
text plus full-output marker and metadata. It does not silently truncate without a
full capture. The seven-day retention worker is not yet wired. Image resizing and
full context compaction/recovery remain separate work.

Only isolated compilation/static checks were performed. No tool, read hook,
formatter, provider, database, VCS command, application or test was executed.
