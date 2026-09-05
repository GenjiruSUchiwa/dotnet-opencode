# CLI/TUI source implementation pass

## Scope and preserved ownership

This pass changes only owned `src/OpenCode.Cli` source and this note. It does not
change Auth/AuthCommands.cs, the CLI project file, generated files, grammar/theme/
license/native bytes, OpenTui, Core, Server, Client, Schema, or Protocol. It preserves
the parent's `ImageSourceLoader` SequenceBuffer stream collector and the
`dotnet-opencode` user agent. No Git operations, tests, live state inspection, or
publication are performed.

## Implemented fixes

### Command and admission flow

- The root uses one source-name/alias table for local slash completion and direct
  no-argument dispatch. Direct local actions clear the originating editor before
  navigation, rather than clearing whichever tab an action has just selected.
- An unknown slash-prefixed input falls through to ordinary typed prompt admission
  when the command catalog is available, matching the source submission fallback.
  A known catalog failure remains an error rather than pretending a command is absent.
- Server command submission captures a full `PromptEditDocument`, including native
  metadata, attachment bindings, mark identities/next ID, and mode. It clears the
  submitted metadata synchronously. Failure restores the complete document only
  into an empty matching origin view; newer typing and unrelated routed Sessions
  are not overwritten. Closed/replaced views are not resurrected.
- A retry-bound prompt cannot be reinterpreted as a new server command. Commands
  consult the existing admission availability before clearing their draft.
- Command POST failure invalidates the same Session observation for reconciliation.
  Acknowledged POST plus failed refresh reports a read error instead of restoring a
  runnable command. There is no automatic command POST retry or invented inbox ID.
  Ordinary prompt availability now also honors `NeedsReconciliation`, consistently
  with the existing command/shell admission checks.

### Model and agent selection

- Choosing a model/agent on Home no longer creates an empty server Session. The
  host retains an explicit local creation preference and returns real catalog/
  readiness-derived presentation. Actual admission still owns Session creation.
- Home presentation uses the chosen model and agent rather than replacing them with
  default labels during refresh. Agent acceptance checks the returned agent and
  restores prior creation preferences on failure.
- Existing model preference persistence, exact-model acceptance, variant behavior,
  and focused Provider/Favorite dispatcher are retained, not replaced.

### Permissions and forms

- Permission tool context is resolved using the request's own Session/message/tool
  IDs, not solely the visible parent transcript. A missing child context is loaded
  through the existing shared Session observer, with revision-driven updates and
  explicit source errors. No second SSE subscription or fabricated tool is used.
- Allow once, Always allow/confirm, and Reject are real pointer actions as well as
  keyboard actions. Both paths call the same selection function and respect the
  actual busy/submitted state, persistence capability, and save-pattern gates.
- Forms and integration text prompts receive the existing host clipboard reader
  through their explicit paste callback. The callback requests text only and keeps
  empty, unsupported, cancelled, timeout, limit, and failed outcomes distinguishable.
  It performs no startup clipboard read and does not replace the copy writer.

### Production transcript and settings

- The supplied native `ImageStrip` is mounted for admitted user image attachments
  and actual tool image content. The `session.image_preview` source setting controls
  the consumer, defaults to false, and uses the existing settings writer.
- User images use admitted bytes and the supplied source deduplication; tool images
  use only actual image-MIME/data-URI tool content. Thumbnail clicks reach the real
  preview dialog without also opening message actions. Loaders are host-retained,
  not created per row/render. The parent's stream collector is unchanged.
- `session.grouping` now controls actual exploration-row construction. Changing it
  rebuilds rows even when the message-list identity is unchanged; the row picker
  uses the same grouping choice. `session.toggle.exploration_grouping` changes the
  existing registered setting. Global and per-row expansion sets remain authoritative.
- Prompt text input, paste, and completion now share the root modal-presence check.
  Activity focus restoration waits until modal/terminal selection overlays close,
  instead of requesting prompt focus over a newly opened dialog.
- Failed active-session reads no longer replace the previous active projection with
  an empty set. Session-picker loading reports failure while retaining prior state.

## Upstream source used

- `packages/tui/src/component/prompt/index.tsx`: synchronous capture/reset, command
  and ordinary-prompt branches, restoration, metadata/attachments and delivery.
- `packages/tui/src/app.tsx`: local slash names/aliases and action registration.
- `packages/tui/src/context/local.tsx`: Home model choice is local creation state.
- `packages/tui/src/routes/session/index.tsx`: grouping, permission source ownership,
  user/tool image extraction, SessionImages geometry and click behavior.
- `packages/tui/src/component/dialog-config.tsx`: session grouping and image-preview
  settings; existing .NET settings controller/store provide persistence.
- Supplied CLI ImageStrip, TranscriptImages, ImagePreviewController, FormComposer,
  PermissionComposer, and shared Session observation/admission implementation.

## Remaining source gaps and counterpart boundaries

- This is not a complete CLI/TUI port. External-editor flows, fuller slash argument
  commands, Home project switching, and collapsed pasted-text reconstruction remain.
  Stash still rejects unsupported pasted descriptors instead of reducing them to text.
- Existing Session model selection is still committed through the typed server
  switch operation, rather than the source's full draft-selection/commit pipeline.
  Location+agent-specific Home preference partitioning also needs a further pass.
- A tab currently owns a family view; complete independent drafts for every nested
  child route need more work. This pass prevents command failure from overwriting
  a different selected route but does not invent a new route-draft storage model.
- Permission edit previews still use the existing text presentation, not the full
  dedicated native patch view. Permission body sizing and narrow-terminal behavior
  remain runtime-unverified. The source-linked actions/context are now connected.
- Server command protocol input has no new caller correlation/metadata envelope in
  this pass. Any public transport change belongs with the Client/Protocol/Server
  owners; native editor metadata is preserved locally, not sent through invented fields.
- Persistent PTY deployment, shell/job ownership, optional Job projections, native
  image protocols, grammar coverage, and the public standalone-server lease remain
  their owners' boundaries. No local PTY/process substitute or second service was added.
- No synthetic provider/MCP/LSP health or completion state was added. A stale/error
  observer is not claimed to be healthy; unsupported paths retain explicit errors.

## Verification and freeze

Source/dependency/build inspection only. No tests are added, edited, or run. No CLI,
help, TUI, SDK/DI, provider/MCP/PTY, database/SQL/migration, clipboard, codec/native,
or WASM operation is executed, and no live configs/credentials are read.

The completion build uses only the pinned repo-local .NET 11 SDK and a fresh
artifact directory, with offline restore sources and OpenAPI document execution off:

```powershell
& .\.dotnet\dotnet.exe build .\src\OpenCode.Cli\OpenCode.Cli.csproj `
  --artifacts-path C:\tmp\opencode\cli-pass-726bd640-f5d5-4704-a4a0-c5f9ff9d8145 `
  -p:RestoreSources=C:\Repos\hona\opencode-dotnet\build -p:NuGetAudit=false `
  -p:OpenApiGenerateDocuments=false --nologo
```

The final handoff build succeeded with **0 errors and 10 warnings** in concurrently
owned Schema/Core files. There were **no CLI warnings**. Source/build edits were
frozen at handoff. Compilation alone establishes no runtime, visual, native,
durability, or performance parity.
