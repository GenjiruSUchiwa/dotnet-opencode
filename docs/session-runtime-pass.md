# Session runtime pass

## Scope

This pass changes non-persistence code under `src/OpenCode.Core/Session` and
`src/OpenCode.Sdk/OpenCodeClient.cs`. It does not change Transfer, Statistics,
SessionQueries, ReadInstructionLoader, OwnedSdkHost, project files, or EF-owned
files. Git integration belongs to the parent.

## Analyzer and lifetime changes

- Configure asynchronous calls, stream enumeration, and lease disposal without
  capturing a caller's synchronization context. Optional tool leases still release
  at the same scope boundary; loaded form/permission leases release on failures too.
- Keep deferred coordinator/title startup asynchronous with
  `ConfigureAwaitOptions.ForceYielding`. ExecutionContext and AsyncLocal admission
  ownership still flow; callbacks are neither removed nor replaced.
- Await cancellation callbacks before disposing their sources. Join owned work in
  `finally`, including when cancellation callbacks fail. Existing bounded publication
  and settlement tokens remain independent of provider/tool work cancellation.
- Make shutdown bookkeeping waits explicitly non-cancellable. No new blanket
  suppression, discarded callback task, or second execution owner is introduced.
- Use non-backtracking key/lineage regular expressions and explicit file attribute
  checks. Parent review retained the existing tool-output configuration error text
  through one method-scoped MA0015 exception rather than appending a CLR parameter suffix.

## Source-backed runtime corrections

Reference paths are relative to `C:/Repos/sst/kind-nebula/packages/core/src`.

| Reference | Correction |
| --- | --- |
| `session/runner/step.ts`, `session/runner/llm.ts` | Add one `RecoverFull` attempt after a transport `RetryFull` or `RotateAndRetryFull` rejection before output. Reload projected context without admitting another item, changing the assistant ID, advancing the logical step, or consuming the generic retry budget. Reset this allowance only for a new logical step. Native requests already carry full context; transport rotation remains the adapter's responsibility. |
| `session/runner/step.ts` | Drain the remainder of a provider response after an emitted error while ignoring later provider content. Do not turn that event into early enumerator disposal. Keep thrown transport failures separate. |
| `session/runner/step.ts`, `session/runner/retry.ts` | Allow retryable post-output failures to use durable continuation, not only incomplete-stream/read failures. Preserve the requested explicit retry-header precedence and bounded retry accounting. |
| `session/runner/publish-llm-event.ts`, `session/runner/step.ts` | Use emitted-provider, unknown-tool, missing-result, and interrupted-step error vocabulary. User declines and cancelled tools settle the assistant as aborted rather than unknown. Record non-object tool arguments as `{ value: ... }` while passing original arguments to the actual tool validator. |
| `session/runner/step.ts` | Move successful tool-output truncation into the independent completion scope. Work cancellation must not discard a result after the tool has completed. |
| `session/runner/to-llm-message.ts` | Lower streaming/running calls without inventing tool results. Parse streaming JSON when complete; otherwise retain the original input string. Preserve provider/model metadata eligibility for settled hosted results. |
| `session/compaction.ts`, `session/model-request.ts` | Apply embedding-host request identity to compaction and preserve the source's emitted compaction error classification. |

The review also traced coordinator admission/registration, ordered move/compaction
controls, step allowances, promotion resets, title selection/fallback, background
synthetics, subagent completion ownership, prompt preparation, archive adapters,
instruction entries, and SDK forwarding. The pass does not claim to replace the
source's unavailable plugin or transport integrations.

## Integration boundaries and remaining gaps

- Provider request lowering currently rejects `PromptCacheKey` for Anthropic and
  Google. Generic Session generation already supplies this source field and can
  fail on those routes. The provider owner must resolve that contract before cache
  lineage can be applied consistently to normal steps/title/compaction. This pass
  does not silently drop the field or edit provider lowering.
- `RecoverFull` handles the existing native transport recovery directive. It does
  not add WebSocket connection state, transport rotation, or a new model loop.
- Configured plugin producers/hooks, explicit workspace execution, and remote or
  managed tool-file materialization still require their actual adapters. Existing
  explicit failures remain; no successful substitute was added.
- Persistence transactions, execution/replay claims, event codecs/projectors, and
  database warning cleanup remain owned by EF/integration. No table or SQL code was
  edited in this pass.
- `SdkHostOptions.Identity` is unchanged. It still derives its default User-Agent
  from the parent's `OpenCodeChannel.UserAgent` (`dotnet-opencode`) and real build ID.

## Verification

Only source inspection, restore, and pinned .NET 11 builds were used. No tests were
added, changed, or run. No application, SDK host, DI container, EF model, database,
SQL, migration, provider, MCP, PTY, or other runtime operation was executed.

Build artifacts: `C:/tmp/opencode/session-runtime-pass-20260905-a`.
Build logs: `C:/tmp/opencode/session-runtime-pass-build-*.log`.
All builds disable OpenAPI document generation. Final full SDK dependency-graph
rebuild: **0 errors, 3 warnings, no owned Session/SDK warnings**.
The remaining diagnostics belong to `OpenCode.Schema/Config/ConfigDuration.cs`
(MA0009, MA0023) and `OpenCode.Schema/WorktreeJson.cs` (MA0009), outside this scope.
Final log: `C:/tmp/opencode/session-runtime-pass-build-final.log`.
This verifies compilation, not runtime behavior.

## Pass 2: request lineage and execution boundaries

The parent-reviewed `SessionToolOutput.FromConfig` exception handling is unchanged:
the original exception message is retained through its documented method-scoped
MA0015 exception. No provider, EF, Transfer, Statistics, project, or composition
file was edited by this pass.

### Cache and request assembly

The provider owner resolved the pass-1 cache blocker while this pass was in
progress. The current `LlmRequest.PromptCacheKey` contract is a generic lineage
hint: Chat/Responses lower it, while Anthropic/Gemini accept it without inventing
a wire field or a cache resource. The earlier cache-blocker note above is therefore
superseded; there is no pending caller patch.

- `SessionRequestIdentity.PromptCacheKey` implements
  `session/model-request.ts:promptCacheKey` once for all callers. It uses
  `session.Fork.SessionId ?? session.Id`, stripping `ses_` only for the canonical
  64-lowercase-hex form. It does not use subagent ParentId or infer a nested fork's
  root ancestor, matching the source's explicit lineage limitation.
- Normal steps, standalone generation, title attempts (including fallback), and
  manual/automatic compaction all pass that same lineage hint. Request preparation
  does not drop cache keys or implement transport rotation. Provider lowering still
  owns provider-specific treatment.
- Normal request System parts now omit empty strings, matching
  `SessionModelRequest.baseTranscript`. The instruction epoch's persisted Initial
  remains separate from chronological instruction-update messages in history.

### Complete boundary flow

Reference: `session/runner/llm.ts:advanceToStep`, `session/context.ts:select/load`.

Normal execution now observes tools/agent/instructions and prepares the instruction
baseline, promotes eligible input, then resolves the model and loads the projected
request context. It no longer resolves credentials/routes or lowers historical
messages before promotion. Thus an unavailable initial instruction baseline still
leaves input pending, while a model-selection failure occurs after input becomes
visible, as in the source. Existing execution-readiness checks remain intact.

At a non-continuing queued/idle boundary, the runner restores entry state and the
step allowance. Promotion uses the general drain scope only at an entry without a
continuation; otherwise it remains steer-only. In particular, cancellation or
reclassification of a steering item between selection and promotion must not make
a continuing steering boundary consume a queued prompt. Physical-attempt retries
skip this boundary reset and all promotion, retaining their generic retry budget,
logical step, and recovery allowances.

Move and compaction continue through their existing canonical operations. The
runner reloads destination selection after a move and persisted context after
compaction; it does not reconstruct/reset instruction epochs or fork projections.
The review also traced subagent foreground/recovered result selection and
background notification admission against `tool/plugin/subagent.ts`,
`session/execution/restart.ts`, and `session/subagent-completion.ts`. Their shared
JobRuntime ownership, original result text, notification metadata/identity, and
admission-before-completion-marker-removal ordering are retained.

### Interrupt continuation and SDK adapter

Reference: `session/execution.ts:interrupt`.

`Continue = true` checks and wakes eligible steering/control work even if the
Session has no active owner to interrupt. The returned boolean still reports
whether an owner was interrupted; it does not report that the wake completed.
Queued prompts stay parked, and controls behind a queued prompt do not overtake it.
Ordinary interruption remains a no-op for known idle Sessions.

`OpenCodeClient` now forwards interrupt options. Owned clients use their host's
stopping token for continuation. Injected clients can supply an explicit lifetime
through the four-argument overload; non-continuing interruption needs no owned host.

### Verification boundary

Only source inspection and pinned .NET 11 restore/build operations were used.
No tests, application/SDK/DI construction, serializer execution, EF/SQL/database,
provider/native/MCP/PTY execution, production data, credentials, or Git operations
were used. Artifacts are isolated at
`C:/tmp/opencode/session-runtime-pass2-20260905-a`; logs use
`C:/tmp/opencode/session-runtime-pass2-build-*.log`.

Configured plugin integrations, explicit workspace execution, and remote/managed
tool-file materialization still require their actual owners' adapters. This pass
does not replace those failures with successful no-ops.

Final pass-2 full SDK dependency-graph rebuild after the exact provider-handoff
helper-name alignment: **0 warnings, 0 errors**. Log:
`C:/tmp/opencode/session-runtime-pass2-build-final.log`.
Code is frozen for parent integration; runtime behavior remains unexercised.
