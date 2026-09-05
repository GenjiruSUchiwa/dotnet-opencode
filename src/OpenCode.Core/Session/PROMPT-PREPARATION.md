# Local prompt preparation and admission

## Shared API

`SessionPromptPreparation(SessionStore, NormalizePromptImage?)` supplies:

- `PrepareAsync(LocationRef, PromptInput, ct)` for explicit preparation only.
- `AdmitAsync(sessionId, input, id?, metadata?, delivery?, ct)` for the complete
  reconcile/prepare/admit boundary. Hosts should normally use this operation.

`SessionExecutionEngine.AdmitPromptAsync` and SDK `AdmitPromptAsync` delegate to
that same boundary. Engine/SDK `PromptAsync` now also accepts a typed `PromptInput`;
the existing string overload remains compatible. The engine's optional final
`prompts:` constructor argument and the SDK's matching argument let hosts supply
the image adapter without constructing another Session store.

Source `session/session.ts` orders get/reconcile, `SessionPrompt.prepare`, staged
revert commit, durable admission, then advisory wake. The native API reconciles
before reading files or producer configuration. A matching pending/delivered ID
returns the original prepared attachments, metadata and payload, not a newly read
file. Admission rechecks identity transactionally. Preparation failures do not
admit input. With the real `commitRevert` callback, the staged revert is committed
only after successful preparation and before admission. Without host composition,
the existing explicit guard remains; see `REVERT-SNAPSHOTS.md`.

Core PromptAsync no longer invokes instruction readiness before admission. Text
and attachments are admitted before execution resolves the model, agent, tools,
and instruction epoch. An unavailable configured model is an execution failure,
not an attachment-preparation failure. `CheckReadinessAsync` remains an optional
explicit inspection API, not part of prompt admission.

## Actual local preparation

- Source `session/prompt.ts`: files use bounded concurrency of eight and preserve
  input order. Name, description, mention, source URI, agent attachments and caller
  metadata retain their canonical Schema fields. Arrays/metadata are captured
  before asynchronous preparation. Skills retain first-occurrence prepared text;
  duplicate IDs retain their name/mention but do not repeat skill text.
- Source `mime.ts`: byte signatures identify PNG/JPEG/GIF/WebP/BMP/AVIF/PDF; text
  detection checks NUL, UTF-8 decoding and the source control-byte ratio. Input MIME
  labels and file extensions are not trusted as content detection.
- Data URLs support strict canonical padded base64 and percent-decoded UTF-8.
  Malformed escapes/base64 fail preparation. The 20 MiB source attachment bound
  applies to decoded input bytes. File reads enforce the same bound while reading.
- Local `file:` URI reads use real BCL filesystem I/O. Text `start`/`end` selection
  is one-based start/inclusive end, applied after input size validation. The common
  decimal numeric query form is supported. Files are not routed through tool
  permission prompts: the user explicitly supplied the attachment, as in source.
- Directory attachments contain visible and hidden regular entries, directories
  before files, with directory separators. Links/device entries are excluded.
- Skills use the existing `InstructionCatalog` and `SkillTool.ToModelOutput`
  renderer, plus source-style first-ten sorted resource sampling. Prompt skill
  preparation does not impersonate a tool call or auto-approve a tool permission.

Plugin preparation is not stubbed: the shared producer configuration adapter
checks actual normalized documents and discovered plugin directories. Configured
or discovered plugins fail explicitly because the native prompt hook/supervisor
is unavailable. The preparation path does not resolve model availability or load
the instruction epoch to make that decision.

## Images and model context

`NormalizePromptImage` is an awaited `ValueTask` callback for a real image adapter.
Only its Data/Mime output replaces the original attachment; provenance and caller
fields remain unchanged. Without an adapter, preparation follows source
`Image.ResizerUnavailableError` fallback: preserve original bytes. It does not
claim to decode, resize, or enforce pixel dimensions. BMP/AVIF must normalize to a
supported type or fail; opaque binary/other MIME types fail instead of disappearing.

`SessionHistory` follows `runner/to-llm-message.ts`: skill text precedes prompt
text; text/directory attachments use the exact source labels and descriptions;
file-backed image/PDF content includes the source path label and structured media.
Duplicate inline images with the same mention and metadata are suppressed only
in model context, not in storage.

`model-request.ts` preparation uses authoritative catalog input modalities for
the exact unsupported-image/PDF error text. It also applies the source oldest-first
25 MiB trigger / 15 MiB target image removal with the explicit no-memory-claims
notice. This includes structured tool-result images and does not mutate history.

## Server owner handoff

Replace endpoint instruction/model preflight plus direct text admission with
`engine.AdmitPromptAsync(sessionId, input, id, metadata, delivery, ct)`. Call
`WakeAsync(sessionId, hostLifetime)` only after it returns and only when resume is
enabled. The returned value is the actual committed/reconciled inbox item. Do not
call PrepareAsync before reconciliation or emit a fabricated message/event DTO.
Map `PromptAttachmentException` and `PromptSkillNotFoundException` through the
source attachment/skill error responses. No Server endpoint was edited here.

## Exact remaining limits

- File URI reads currently require Windows regular-file classification and reject
  direct UNC paths. Unix special-file classification and nonlocal/workspace FSUtil
  placement need a real filesystem adapter; they are not guessed from file names.
- Decimal query numbers are supported; JavaScript Number's radix-prefixed query
  syntax and exact cross-platform localeCompare directory ordering need completion.
- No native pixel normalization adapter is supplied. The source's explicit
  resizer-unavailable fallback is used, not a fake successful resize.
- Generic LLM metadata now represents user metadata, agent attachments and image/PDF
  descriptions directly. The former execution guards are removed; see
  `HISTORY-METADATA.md`. Text/directory descriptions keep their exact source rendering
  and attachment annotations. Provider interpretation remains with the LLM owner.
- Plugin flush/prompt hooks, remote attachments, unsupported MIME types and advanced
  placement remain unsupported. Staged-revert commit requires the real host callback.

Verification: repository .NET 11 SDK/Core/Schema build, isolated under
`C:\tmp\opencode\core-finish-pass`. No tests, runtime filesystem/database reads,
provider/tool calls, process control, or application launches were performed.
