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
