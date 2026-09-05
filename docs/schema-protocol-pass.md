# Schema and Protocol contract pass

Status: implementation checkpoint complete; frozen for parent integration.
Date: 2026-09-05.

This pass changes only handwritten Schema/Protocol source and this document.
It does not change `OpenCodeChannel.cs`, project/global build configuration,
canonical OpenAPI assets, generated files, packaging, persistence, or callers.
It is a source-and-build checkpoint, not a claim of complete runtime parity.

## Implemented contracts

- Required JSON fields no longer silently become zero, false, or null in the
  changed stats, filesystem, diff, VCS, location, transfer, reference, permission,
  command, plugin, instruction-entry, and protocol response contracts.
- Changed optional fields omit native null values during encoding and reject
  explicit JSON null where source accepts only omission. This includes Session
  info/revert, create/prompt/move/fork/import requests, PTY creation, permissions,
  worktree payloads, and cursor fields. Existing signatures remain intact.
- Required object/list/string values are checked on encoding where the context's
  null-omission setting would otherwise hide invalid required data. Changed lists
  reject null elements. PTY environment values must be strings.
- Integer converters enforce the source positive/nonnegative domains without
  changing existing Int32 fields. They accept numeric integer spellings such as
  exponent notation, reject fractional/nonfinite values, and fail on native
  representation overflow. Stats median duration remains finite, not necessarily
  positive; money/token semantics were not tightened to nonnegative values.
- Six existing enum codecs now accept only exact case-sensitive source strings:
  file-diff status, filesystem entry type, LLM finish reason, permission reply,
  stats tool mode, and VCS file status. Integer enum tokens are rejected.
- Permission sources require `type: "tool"`. Plugin info validates its active
  versus failed required fields without replacing its existing constructor.
  Health requires `healthy: true`. Worktree errors require `name: "WorktreeError"`
  and non-null data. No synthetic success or error responses were introduced.
- Instruction entries require the source key pattern and a present JSON value;
  JSON null remains valid. Value-size enforcement remains with the runtime owner.
- Added canonical current event definitions for `filesystem.changed`,
  `permission.asked`, `permission.replied`, `plugin.added`, `plugin.updated`, and
  `websearch.updated`. These are pure payload/codec definitions, not publishers.
- Added shared transitional current-client contracts for `session.status`,
  `session.idle`, `tui.prompt.append`, `tui.command.execute`, `tui.toast.show`, and
  `tui.session.select`, matching upstream manifest membership and relative order.
  `session.status` retains the explicit `SessionStatusUpdated` identifier.
  Arbitrary TUI command IDs remain valid. Toast duration defaults to 5000 only
  while decoding; native construction requires a duration and encoding validates
  it as a positive integer. These events are not added to the durable inventory.
- The original `SessionIdleEventData(SessionId, Outcome)` remains unchanged for
  compatible callers. The public transitional payload uses the distinct
  `SessionStatusIdleEventData(SessionId)` and emits no outcome. An intermediate
  duplicate declaration was removed before the successful final builds.
- The handwritten `OpenCodeJsonContext` declaration registers the new payloads
  and nested contracts used by property codecs. No generated output was edited.

## Regex diagnostic cleanup

`Config/ConfigDuration.cs` uses named `number` and `unit` captures with explicit
capture and the non-backtracking engine. `WorktreeJson.cs` uses the same engine
for its existing replacement expression. The expressions keep their original
anchors, character ranges, number/unit grammar, capture values, and trim output.
Neither call adds an arbitrary match timeout, a catch that changes errors, or
an analyzer suppression. Existing invalid-input exceptions remain unchanged.
The default timeout selection is retained; pathological execution timing is not
a runtime-tested guarantee.

The three reported Schema warnings (MA0009 twice and MA0023 once) are absent from
both successful build checkpoints below.

## Vogen and compatibility holds

Source inspection found all 25 wrappers already using Vogen. This pass preserves
their factories, scalar encodings, initialization rules, and analyzer policy;
there is no second migration or analyzer suppression to apply.

The following source mismatches were reported for parent coordination and were
not silently changed:

| Contract | Native behavior retained | Proposed source behavior |
| --- | --- | --- |
| `SessionId.Create()` | Ascending identifier | Descending identifier |
| `MessageId` | Non-whitespace string | Prefix `msg_` |
| `PermissionId` | Non-whitespace string | Legacy loose prefix `per`, not `per_` |
| `WorkspaceId` | Non-whitespace string | Legacy loose prefix `wrk`, not `wrk_` |
| `PermissionSavedId` | Rejects empty/whitespace | Any non-null string |

These changes affect existing identifiers, factories, or consumer assumptions.
They need a compatibility decision and a coordinated native schema-export update.
There is no ID rewrite, fake absence sentinel, storage relabeling, or migration.
Missing source directional/from-event convenience factories were not invented at
call sites. Remaining public numeric fields that use Int32 instead of JavaScript
numbers also need caller coordination before changing their C# representation.

## Server-owner handoff

No caller signatures must change to compile this checkpoint. The full CLI build
confirms source compatibility with the integrated dependency graph.

`src/OpenCode.Server/Documentation/NativeSchemaExporter.cs` needs source-owner
follow-up before claiming native OpenAPI parity:

1. Map `TuiToastEventJsonConverter` / `TuiToastShowEventData`: required string
   `message`, required exact `variant` enum (`info`, `success`, `warning`, `error`),
   optional string `title`, and positive-integer `duration` with decoding default
   5000. The native encoder always writes duration. This custom converter is not
   inferable as a normal record by the existing exporter.
2. Reflect callback-enforced literal/conditional constraints where the exporter
   currently sees plain C# fields: health true, permission source tool, plugin
   active/failed requirements, worktree error name, and instruction-entry key.
3. Register/adopt new event definitions in external consumers only where their
   domain needs them. This pass does not add emission, subscriptions, or UI work.

The integer converter family names retain the exporter's existing positive and
nonnegative mappings. Exact enum codecs use the existing enum export path.
The canonical OpenAPI document and hash were not changed to conceal differences.

## Verification

Only source inspection, restore, and pinned-SDK builds were used. No tests were
added, changed, or run. No application, serializer, codec, SDK, DI, EF model,
database, migration, native library, provider, or terminal runtime was executed.

Pinned SDK: `11.0.100-preview.7.26381.103`, invoked through repository-local
`.dotnet/dotnet.exe`. All verification used `OpenApiGenerateDocuments=false`.

| Build | Result | Evidence |
| --- | --- | --- |
| Schema + Protocol | 0 warnings, 0 errors | `C:\tmp\opencode\contracts-pass-20260905-02.log` |
| Full CLI dependency graph | 0 warnings, 0 errors | `C:\tmp\opencode\contracts-pass-20260905-final.log` |

Final command, from `C:\Repos\hona\opencode-dotnet`:

```powershell
& .\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj `
    --artifacts-path C:\tmp\opencode\contracts-pass-20260905-final `
    -p:OpenApiGenerateDocuments=false -p:NuGetAudit=false --verbosity minimal
```

Outputs stay under `C:\tmp\opencode`. Builds did not produce or publish a tool
package. There were no Git, staging, commit, push, remote, or installation actions.

## Exact changed source files

Paths below are relative to the physical repository root.

### Schema

```text
src/OpenCode.Schema/ClientEventDefinitions.cs
src/OpenCode.Schema/Command.cs
src/OpenCode.Schema/Config/ConfigDuration.cs
src/OpenCode.Schema/CurrentFeatureEventDefinitions.cs
src/OpenCode.Schema/EventManifest.cs
src/OpenCode.Schema/FileDiff.cs
src/OpenCode.Schema/FileSystem.cs
src/OpenCode.Schema/Instruction.cs
src/OpenCode.Schema/Llm.cs
src/OpenCode.Schema/Location.cs
src/OpenCode.Schema/Permission.cs
src/OpenCode.Schema/PermissionSaved.cs
src/OpenCode.Schema/Plugin.cs
src/OpenCode.Schema/Reference.cs
src/OpenCode.Schema/Serialization/OpenCodeJsonContext.cs
src/OpenCode.Schema/Serialization/SourceContractJsonConverters.cs
src/OpenCode.Schema/Serialization/TuiToastEventJsonConverter.cs
src/OpenCode.Schema/Session.cs
src/OpenCode.Schema/SessionRevert.cs
src/OpenCode.Schema/SessionStats.cs
src/OpenCode.Schema/SessionTransfer.cs
src/OpenCode.Schema/Vcs.cs
src/OpenCode.Schema/WorktreeJson.cs
```

### Protocol

```text
src/OpenCode.Protocol/Groups/HealthProtocol.cs
src/OpenCode.Protocol/Groups/PermissionProtocol.cs
src/OpenCode.Protocol/Groups/PtyProtocol.cs
src/OpenCode.Protocol/Groups/ServerProtocol.cs
src/OpenCode.Protocol/Groups/SessionArchiveProtocol.cs
src/OpenCode.Protocol/Groups/SessionForkProtocol.cs
src/OpenCode.Protocol/Groups/SessionMoveProtocol.cs
src/OpenCode.Protocol/Groups/SessionProtocol.cs
src/OpenCode.Protocol/Groups/WorktreeProtocol.cs
```

Documentation: `docs/schema-protocol-pass.md`.

## Remaining limits

- Runtime JSON acceptance, round trips, error mapping, native OpenAPI export,
  persisted-row compatibility, and current-client behavior remain unverified.
- This broad cross-contract audit is not an exhaustive proof for every branch of
  every handwritten codec. Existing specialized form/config/model/provider,
  prompt/message/inbox, terminal, and durable-event codecs remain distinct;
  compile success is not a behavioral certification of those implementations.
- Event inventory completeness flags remain false. Unimplemented shared/V1 and
  remaining durable contracts are not classified as invalid or private merely
  because they are absent. No `isServer` rejection filter was added.
- Native `DateTimeOffset` range and Int32 bounds are narrower than the source
  representations. Existing native-only convenience records are not claimed as
  canonical source contracts. Runtime concerns already present in Schema (for
  example tool callbacks) were not moved across owner boundaries in this pass.
- Not every flattened native union has source-equivalent excess-property
  normalization. For example an active native `PluginInfo` can retain an extra
  error field. Arbitrary raw JSON and JavaScript/.NET regex edge equivalence also
  need dedicated, separately authorized behavioral verification.
- Regex warning cleanup does not assert measured performance or runtime timeout
  equivalence under every host-wide regex configuration.

The parent owns integration, Git operations, any follow-up coordination, and
publication. This worker is frozen after this checkpoint.

## Pass 2 — usage notification and stateless generation

Status: source/build checkpoint complete; frozen again for parent integration.
The parent resumed this handwritten-contract scope after integrating pass 1.
All ID factory, generation-direction, and validation changes remain on hold.
No Vogen wrapper, generated artifact, canonical asset, project configuration,
`OpenCodeChannel.cs`, caller, runtime publisher, or endpoint handler was changed.

### Persistence publisher handoff

Added the exact ephemeral contract from `packages/schema/src/session-event.ts`
(`UsageUpdated`, lines 147–155; inventory membership at line 628):

```csharp
// namespace OpenCode.Schema
SessionUsageUpdatedEventData(SessionId SessionId, Money Cost, TokenUsageInfo Tokens)
SessionEventDefinitions.UsageUpdated
// EphemeralEventDefinition<SessionUsageUpdatedEventData>
OpenCodeJsonContext.Default.SessionUsageUpdatedEventData
```

Payload JSON contains required `sessionID`, `cost`, and `tokens`, with their
existing canonical scalar and token codecs. There is no source, message ID,
timestamp, or durable envelope added to the payload. Existing event factories
still supply the event-level ID/time/location/metadata.

`UsageUpdated` is placed after `Created` among the currently implemented Session
definitions, preserving source-relative order. The existing manifest composition
includes it in public/shared definitions. The durable inventory filters it out;
`UsageRecorded` remains a separate durable fact and is unchanged. No publication
or projection behavior was implemented in Schema.

### Core/Server stateless generation handoff

Source: `packages/protocol/src/groups/generate.ts` and `packages/core/src/generate.ts`.

```csharp
// namespace OpenCode.Schema
GenerateTextInput(string Prompt, ModelRef? Model = null)
GenerateTextResult(string Text)

// namespace OpenCode.Protocol.Groups
GenerateTextResponse(GenerateTextResult Data)
GenerateEndpoints.Text              // "/api/generate"
GenerateEndpoints.Operation         // "generate.text"
GenerateEndpoints.OpenApiOperation  // "v2.generate.text"
GenerateProtocolJsonContext.Default.GenerateTextInput
GenerateProtocolJsonContext.Default.GenerateTextResult
GenerateProtocolJsonContext.Default.GenerateTextResponse
```

Request JSON is `{ "prompt": "...", "model": { ... } }`, with model optional.
Success JSON is `{ "data": { "text": "..." } }`. Prompt/text must be strings;
empty strings remain valid. Explicit-null model is rejected, omitted model is
preserved, and no default model is manufactured by the contract. Existing
`ModelRef` owns model parsing. Core resolves the base-configuration model.

Server must mount **POST** at the route above and declare only the source's
`OpenCode.Protocol.Errors.InvalidRequestError` (400) and
`ServiceUnavailableError` (503). The new context registers these existing types
and their shared `SessionQueryError` base for tagged serialization; that broad
base registration is not a declaration of additional endpoint error variants.
Serialize through base metadata when the `_tag` discriminator is required.
There is no invented generation-specific error hierarchy or fake success shape.

These contracts do not reference `SessionGenerate`, `SessionGeneration`, Session
IDs, Location, persistence, tools, or Session history. The Core owner can consume
`GenerateTextInput` from `Core/Generate`; Server owns mapping runtime failures to
the declared protocol errors. This worker did not edit those packages.

### Additional codec fixes

- `PluginInfoJsonConverter` selects the active/failed branch before reading
  branch-specific fields. Active plugins ignore even malformed excess `error`
  fields and omit error during encoding; failed plugins require string error.
  Required source/tui, conditional ID, optional-ID null rejection, and exact
  status literals remain enforced. Existing constructors and ID domains are
  unchanged. This supersedes the PluginInfo excess-error limit listed in pass 1.
- `QuestionOption`/`QuestionPrompt` require their source string/list fields;
  options reject null elements, and optional `multiple` omits null and rejects
  explicit-null input. Source description text about label/header length is not
  promoted into an invented validation constraint. Empty arrays remain valid.
- `SkillInfo` requires name/location/content and applies omission-versus-null
  rules to description/slash/autoinvoke. `SkillId` is untouched.
- `WorkspaceDestroyResult` requires the boolean `destroyed`; missing JSON no
  longer silently becomes false. Both true and false remain valid. `WorkspaceId`
  is untouched.

The Server/network owner needs an exporter mapping for the new
`PluginInfoJsonConverter`: union of active (required id/source/status/tui) and
failed (required source/status/error/tui, optional id). The two status values are
literal discriminants. The custom converter now owns branch normalization, so
normal-record inference is insufficient. Earlier toast/callback exporter work
remains with that owner; no Server source was changed here.

### Pass 2 changed files

```text
src/OpenCode.Schema/Generate.cs                                  (new)
src/OpenCode.Schema/Plugin.cs
src/OpenCode.Schema/Question.cs
src/OpenCode.Schema/SessionEvent.cs
src/OpenCode.Schema/SessionEventDefinitions.cs
src/OpenCode.Schema/Skill.cs
src/OpenCode.Schema/Workspace.cs
src/OpenCode.Schema/Serialization/OpenCodeJsonContext.cs
src/OpenCode.Schema/Serialization/PluginInfoJsonConverter.cs      (new)
src/OpenCode.Protocol/Groups/GenerateProtocol.cs                  (new)
docs/schema-protocol-pass.md                                    (append)
```

### Pass 2 verification and limits

- Initial Schema/Protocol build: **0 warnings, 0 errors**.
  Log: `C:\tmp\opencode\contracts-pass2-20260905-01.log`.
- Final full CLI dependency build, including all pass 2 contract changes:
  **0 errors, 2 warnings**, both outside this worker's ownership:
  `OpenTui.Blazor/Code/CodeChunks.cs:33–34`, MA0015 (expression does not match a
  parameter). Schema and Protocol produced no warnings. Those warnings were
  reported to the parent and left untouched.
  Log: `C:\tmp\opencode\contracts-pass2-20260905-final.log`.
- Pinned repository SDK: `11.0.100-preview.7.26381.103`.
  Builds used isolated artifact directories under `C:\tmp\opencode`,
  `OpenApiGenerateDocuments=false`, and `NuGetAudit=false`.
- No tests, application/serializer/codec execution, DI/EF/database operations,
  native/provider/terminal execution, global installation, or publication.
  No Git, staging, commit, push, or subdelegation actions.
- Endpoint mounting, runtime model selection/failure mapping, usage publication,
  actual JSON round trips, native schema export, and compatibility with stored
  data remain unverified by this build-only checkpoint. Inventory completeness
  flags and all pass 1 ID/native-representation holds remain unchanged.

Pass 2 is frozen for parent integration.
