# Local Instruction Epochs

The supported composition is explicit, not a global instruction registry:
environment, date, captured Code Mode, AGENTS discovery, guidance availability,
and API-managed instruction entries. The Session owns observation, admission,
epoch state, and chronological System messages.

`SessionInstructionEntries` now exposes list/put/remove through the SDK. It uses
the existing producer row storage and delayed instruction boundary; no mutation
event is invented. See `../Session/INSTRUCTION-ENTRIES.md` for limits, no-op/null/
tombstone behavior, source mapping, and the Server endpoint handoff.

The engine now binds Jint Code Mode to every captured tool snapshot and passes its
CodeModeDiscovery summary through the existing instruction epoch pipeline. See
`CODEMODE-INTEGRATION.md` for finite native host budgets and restricted-runtime limits.

Transient generation now uses a SELECT-only preview of initial/current instruction
state and settled context. It shares delta rendering with normal preparation but
does not commit blobs, hashes or chronological messages. See `../Session/GENERATION.md`.

## Source Mapping

- `instructions/index.ts`: ordered sources distinguish available JSON, observed
  removal, and temporary unavailability. SHA-256 hashes canonical JSON with ordinal
  UTF-16 key sorting, array order, JavaScript string escaping, and number formatting.
  Available changes add one hash and blob; removals use the literal `removed`.
  Initial unavailable sources block admission. Later unavailable discovery retains
  its last hash instead of silently removing prior instructions.
- `instructions/builtins.ts`: environment and date use the original source keys and
  initial/changed wording. The environment contains the session/location/project
  directory, Git marker status, Node-style platform name, and a created app temp
  directory. Date uses the local `toDateString` shape.
- `config/plugin/instruction.ts`: global config AGENTS first, then nearest-to-
  farthest project AGENTS. Stop includes home when home contains the Location;
  otherwise stop at the project root. Duplicate resolved paths retain first
  insertion order. Missing files are observed absence; discovered unreadable files
  make discovery unavailable. No CLAUDE fallback is invented.
- `instruction-discovery.ts`: original `Instructions from:` blocks and source
  addition/removal wording. Changed files use upstream's full-replacement form;
  the smaller unified-diff optimization is not implemented. Updates freeze their
  text into the durable event, so later requests do not reread historical files.
- `session/instruction-entry.ts`: persisted entries, including tombstones, load in
  key order as `api/<key>` sources. Initial/changed/removed context wording is the
  original source text. JSON values render with two-space JSON formatting.
- `session/instruction-state.ts`: `instruction_blob` stores values by hash once;
  `instruction_state` stores initial/current hash maps plus epoch_start and
  through_seq. Initial publication establishes both maps without a visible System
  message. Later updates change only current_values/through_seq and insert a System
  message when rendered text is present. All writes share the event transaction.
- `session-event.ts` and `session/projector.ts`: `session.instructions.updated.2`
  contains only `{sessionID, delta, text?}`. Chronological message identity derives
  from the event ID, sequence from the event, and description from changed keys.
  Initial live metadata marks `instructions.initial`; it is not repeated in durable
  data. Baseline rendering dereferences stored initial blobs, and history is loaded
  in the same transaction as that baseline.
- `session/model-request.ts` and `session/system-prompt.ts`: use the exact embedded
  upstream system.txt with guidance from captured tools, or
  the resolved AgentCatalog system string. The returned selection supplies that
  system text and the separately persisted epoch baseline for request assembly.
  No generic replacement prompt is used.

## Supported Local Slice

Default runner wiring uses the Config owner's `ConfigLoader.LoadDocument` merged
JsonObject, not the reduced provider DTO. `CheckReadinessAsync` optionally previews
the full composition; prompt admission no longer calls it. Every eligible step selects
and commits instructions before inbox promotion, then loads the stored initial
epoch and projected messages together. Requests carry the real system prompt plus
that stored baseline; later frozen updates remain chronological System messages.
No instructionless opt-in or fallback remains. Engine capabilities describe this
supported local slice; readiness still evaluates each session's actual producers.

The supported default configuration has a resolvable provider/model, implicit-local
placement, ordinary non-linked Location directories, default build agent or a
configured system/permissions agent, global/upward AGENTS files, optional stored API
entries, discovered/configured local skills, and local reference declarations.
Skill and reference guidance now have their real producers and source renderers;
they no longer fail merely because their configuration or directories are present.

`ProducerConfiguration` is a local Config.entries provenance adapter. It delegates
every file's parsing/substitution/normalization to ConfigLoader.LoadDocument and
never re-merges producer definitions. Global, explicit, upward direct, project
.opencode, and virtual documents retain their source order and origin. The virtual
document takes only its own normalized keys from the final substituted snapshot.
Producer fields are atomic in the current ConfigLoader; the adapter must follow
any future change to that contract. Project-disable flags and global-directory
special handling follow the loader. A general Config.entries inventory remains
preferable when the Config owner exposes one.

Last-good producer documents are held by disposable `InstructionLocationState`
instances owned by SessionStore, not a process-static cache. `SessionStore.Dispose`
(including DI/SDK disposal) releases every scope, and
`InvalidateInstructionLocation(directory)` provides explicit Location invalidation.
Read-only catalog requests use request-scoped state and dispose it immediately.
Only producer-relevant fields are retained; provider/auth/header/body configuration
is excluded, and unimplemented domains retain only guard-presence markers. There
is no arbitrary TTL. Persisted instruction blobs/hash maps remain authoritative
and are not changed by disposal or invalidation. The separate upstream Location
activity-eviction service is not implemented here.

Core catalog callers may retain the four-argument
`ProducerConfiguration.Read(location, home, global, merged)` call: it owns and
disposes a request-scoped observation and returns an independent result. A caller
that owns a Location lifetime passes its `InstructionLocationState` as the fifth
argument and disposes/invalidates that state with the Location. SessionStore owns
this path for session instruction reads; catalogs never regain a static cache.

Agent source ownership belongs to `AgentCatalog`, including Markdown agent/mode
discovery and document normalization. Execution readiness passes its resolved
`AgentInfo` into instruction assembly. Callers without that selection (including
agent-selection preflight) resolve through `AgentCatalog.ResolveAsync` first.
System text and skill permissions both use that result, not a second partial agent
parser or retained instruction-policy snapshot. `LocalInstructions` does not reject
agent/mode directories independently. Until the Agent owner's loader is available,
AgentCatalog's own unsupported-source errors still propagate; no user configuration
is skipped. The existing `ResolveAsync(directory, id, ct)` contract is sufficient
for this integration and requires no new cross-owner API.

Core PromptAsync now calls the shared preparation/admission API without execution
readiness. Retry reconciliation precedes attachment reads and producer checks.
The Server owner must use this same boundary instead of instruction preflight;
see `../Session/PROMPT-PREPARATION.md`. UI route/home preservation remains separately owned.

Remaining explicit guards: visible skills without a real captured skill definition
(the host-composed factory now supplies one),
configured instructions file lists, plugins/plugin,
remote HTTP(S) skills, Git references, and auto-discovered plugin definitions.
AgentCatalog and the runner validate their own supported agent settings and sources.
No unsupported producer is silently
treated as absent. Do not delete or disable user policies to bypass these limits.

MCP no longer has a blanket configuration-presence guard. Without a runtime
observation, canonical per-document server definitions replace by name in source
order. No configured servers, or all explicitly disabled servers, produce Removed.
Any enabled server requires an observation and produces Unavailable until the native
runtime is connected to this seam. Invalid configuration still fails validation.
Unavailable observation retains durable values after initialization and blocks an
incomplete initial baseline; it does not fabricate empty MCP guidance.

MCP integration: `SessionStore.SelectInstructionsAsync` accepts an optional
internal `InstructionSource mcp`, forwarded to LocalInstructions. Readiness and
execution await `ToolLocationLease.Mcp.ObserveAsync` before snapshot capture and
pass that exact observation into instruction selection. Configuration is decoded
from the full normalized documents in precedence order and combined by
`McpRuntime.Configure`, not reconstructed from the reduced provider DTO.
`McpInstructionSource.FromObservation`
accepts the runtime owner's actual `McpObservation` and resolved `AgentInfo`, applies
the upstream tool/execute permission filters, and sorts the summaries.
`McpInstructionSource.Create` supplies
the source key and exact initial/change/removal renderers. Its Available value is
the canonical server-sorted Summary array `{server, instructions, codemode?: false}`.
Guidance requires at least one permission-visible server tool, and
`execute` permission when the server uses Code Mode. Do not claim native-tool support
for a default Code Mode server to bypass the missing Code Mode runtime.

Per-server disabled/failed/needs-auth states are settled observations, not global
Unavailable. Source MCP.instructions reads only connected client instructions;
MCP.tools supplies the current tool inventory. No visible instructions means Removed.
Only inability to obtain the complete observation uses Unavailable. No server status,
credential, URL, or duplicated epoch field belongs in the instruction value.

The local root resolver supports ordinary Git-marker roots and non-VCS locations;
it is not the complete Project/Location/VCS service. Location directory links and
explicit workspaces remain unsupported; skill-directory links are supported by
the skill scanner. Source files are rescanned at readiness/step
boundaries rather than through a filesystem watcher. Supported producer detection
is conservative until the complete Config.entries directory inventory is exposed.

Request assembly will not silently omit unknown keys from an inherited epoch or
pretend to render unavailable guidance. Fork epoch adoption, compaction epoch
advance, movement, revert reset, plugin source transforms, and API-entry mutation
endpoints are not implemented by this pass.

Application code performs filesystem reads only when invoked by readiness or
execution. Verification does not invoke discovery or inspect live instructions;
it is limited to isolated builds and static source checks.
