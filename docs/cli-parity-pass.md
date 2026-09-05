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

---

## Pass 2 — selection admission and independent route drafts

This section is separate from the committed pass-1 evidence above. It supersedes
the pass-1 gaps for live-Session draft selections, Location+agent Home model choices,
nested-child editor ownership, and native permission edit previews.

### Source trace and implemented selection policy

- `packages/tui/src/context/local.tsx:142–278, 394–447`: Home models are selected
  from a Location+agent map; Session model/variant choices are local drafts over
  durable Session.model. Local selection does not itself switch the server model.
- `component/prompt/index.tsx:1194–1213, 1282–1385`: capture before clearing; existing
  shell/command branches do not commit the ordinary-prompt selection. Ordinary
  prompts switch the captured agent, commit staged revert, then prepare the captured
  model after earlier admissions. Queue/steer is the captured delivery, not a new
  model-selection policy.
- `component/prompt/draft-stash.ts`: in-progress text belongs to the routed Session,
  not the family tab's display identity. Drafts are client-memory state.
- Existing typed `SessionHttpClient.SwitchAgentAsync`, `SwitchModelAsync`, and
  `CommitRevertAsync` are sufficient. Core `SessionMutations.SelectAgentAsync` does
  not choose the new agent's preferred model; an existing Session's stored model
  remains the base selection. No public wire changes were needed or made.

The root now validates and records model/variant and agent choices locally. The old
TUI host callbacks that immediately mutated an existing Session were removed. The
same preference service/controller still records accepted picker/cycle choices;
hydration and Session navigation do not write recents or favorites.

Home choices use actual `LocationRef` (directory and workspace identity) plus agent
identity. Agent-configured models retain Home precedence, followed by available
recent/default/catalog candidates. A Session instead uses its own local draft or
the actual stored Session.model, never a Home choice or the newly chosen agent's
preferred model. A missing Session model is not replaced with an invented default.
Variant `default` remains no override. Home model entries store the base model so
removing a stored variant preference cannot resurrect an old variant.

### Admission and retry lifecycle

| Action | Implemented ownership and behavior |
| --- | --- |
| Local model/agent choice | No Session mutation. Remains local across refresh/navigation until its matching submission is committed. |
| New ordinary prompt | Captures immutable `PromptSelection` plus the full typed prompt before clearing. New Session creation uses that captured Location/agent/model, not the currently visible route or mutable host defaults. |
| Steer/queue submission | Selection preparation runs under the existing per-Session admission semaphore, after preceding admissions. Uses the real agent switch, staged-revert commit, model switch, then the existing prompt POST. |
| Preparation confirmation | A client-only commit revision is published after real switch acknowledgments and observed through the existing Session read model. Matching submitted drafts clear; a different newer local choice remains. Reconciliation includes hidden routes, not only the visible tab. |
| Unconfirmed retry | Original item ID, payload, selection, and delivery capture remain authoritative. A committed selection is not reapplied merely because the prompt response was lost. Edited unconfirmed payloads are rejected instead of minted as a fresh request. |
| Definitively rejected/cancelled input | An explicit changed payload, delivery, or selection can start a new capture. This exception never applies to unknown outcomes. |
| Existing inbox queue/steer/cancel | Existing typed mutation only. Does not reapply a captured model or manufacture a new prompt/admission. |
| Command/shell in an existing Session | Leaves local ordinary-prompt selections uncommitted, matching the source branches. New-session commands/shells use captured creation choices; no fake command inbox ID or automatic POST retry was introduced. |
| External selection/history updates | Runner step models no longer overwrite the prompt's local choices. Confirmed stored metadata remains authoritative once the matching local submission has been committed. |

`PromptSelection` and selection commit revisions are CLI-only captures/read-model
fields, not new JSON request fields or durable events. Commit watermarks are scoped
to the actual client and Session, and reset when the client changes. The server's
stored-model policy still determines execution: this is not a claim that every
queued item carries an immutable execution-model envelope.

### Route, tab, and cleanup lifecycle

- Canonical Session editor identities are separate from family-tab identities.
  Parent, child, and nested child retain independent text, cursor/selection, undo
  history, prompt-history cursor, attachments, metadata, marks/next IDs, shell mode,
  error state, and retry captures.
- Navigation hydrates the real destination before switching views. Family metadata
  comes from the existing typed family loader/shared observer. The tab remains
  rooted at the real family root; its last routed child is remembered in memory.
- Closing/reopening or opening the same retained Session through the picker reuses
  its canonical editor. Replacing a preview tab does not erase that Session's draft.
  There is no disk format change or invented Session ID for an editor slot.
- Background prompt, command, and shell completions use the captured editor identity.
  Failure restoration cannot target a sibling/parent draft or overwrite newer text.
  Home-to-Session adoption preserves the editor and newer local selections without
  reopening a closed tab. In-flight captures survive tab-alias cleanup.
- Stash, clipboard application, attachment lookup, history, undo/redo, fork restoration,
  and command/shell captures now use the routed editor identity. Retry IDs are never
  stripped to make a stash or command conversion eligible.
- Ordinary prompt failure restores the captured mode as well as text/marks, so an
  intervening empty shell-mode toggle cannot reinterpret the restored prompt as a
  shell command. A retained prompt retry is also rejected by shell submission.
- Session deletion removes its editor mapping, child/family mappings, metadata,
  retry selections, mark controllers, and selection-commit watermarks. Tab aliases
  and Home view state are pruned when no longer retained; Session drafts remain
  in client memory for later reopening, as in the source draft-stash model.
- Stored tab layout remains root Session IDs/titles. Existing title, fork, viewed,
  sidebar, permission/form, and terminal callbacks still use the same client and
  observer; no second SSE or selection-specific configuration writer was added.
- Home form placement now follows the actual Home Location rather than the first
  launch directory. Completion, skills, clipboard validation, media loaders, and
  management/status context checks use the same actual selection Location, avoiding
  reuse of another route's stale readiness presentation after a move/navigation.

### Native permission edit body

The production `PermissionComposer.razor` now mounts the existing `PatchDiff` in a
bounded native `ScrollBox` for actual edit-permission diff metadata. Source precedence
is `metadata.files[0].patch`, then `.diff`, then `metadata.diff`. Pending raw
`input.patchText` uses the existing shared `TuiCode` provider instead of constructing
a synthetic tool result. With neither, it shows the source “No diff provided”.

The view uses resolved elevated diff roles, the existing shared syntax provider,
configured auto/split/unified layout, and the permission source's word-wrap policy.
Page keys operate the native scroll state. Invalid unified patches retain the existing
parser error and exact input display. No intraline algorithm, patch application,
file read, replacement lexer, or parser instance per permission/hunk was added.

### Verification, ownership, and remaining limits

All four pass-2 full CLI dependency-graph builds succeeded. The final build includes
the mode-restoration correction and has **0 warnings and 0 errors**; it also staged
the complete Server runtime. Source/build edits are frozen for handoff. Isolated artifacts:

`C:\tmp\opencode\cli-pass2-2d7ced31-75b1-471d-a367-4492a61e1e14`

Only pinned `.dotnet\dotnet.exe` SDK 11.0.100-preview.7.26381.103 builds and offline
restore were used, with `OpenApiGenerateDocuments=false`. No tests were added,
edited, or run. No CLI/help/UI/app, SDK/DI, live config/credential, API, DB/SQL/
migration, clipboard, codec/native/WASM, provider/MCP/PTY, or process probe was run.
No Git, publication, global install, or subdelegation occurred.

Excluded Auth/project/generated/assets and other packages remain untouched. The
parent's image stream collector, user agent, and production grammar-cache clock
wiring are preserved.

Remaining limits are explicit: these in-memory drafts do not survive a client
restart; command/shell creation still waits for confirmed Session metadata rather
than inventing optimistic Session records; the selection-switch and prompt APIs
are separate server operations, not one atomic cross-client transaction. Their
source ordering is serialized locally, not fenced against another client. No
public counterpart change is proposed for that source behavior. External-editor/
full slash-argument work and collapsed pasted descriptors remain outside this pass.
Focus, layout, streaming interleavings, native diff/image protocols, and end-to-end
execution still require authorized runtime verification. A build proves none of
those parity claims.
