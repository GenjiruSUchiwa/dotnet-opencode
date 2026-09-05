# Skill Tool Integration

`Builtins.SkillTool` implements the native leaf from `core/tool/plugin/skill.ts`.
`Create()` returns the single canonical `ToolInfo` with native name `skill`,
CodeMode false, validated `{ id: string }` input and validated
`{ name: string, directory: string, output: string }` output.

The Location factory installs it alongside read/grep/glob/write/edit. It supplies
the same `PermissionService` used by the other leaves and a real catalog delegate:

```csharp
new SkillTool(permission,
    ct => InstructionCatalog.ListSkillsAsync(location.Directory, ct)).Create()
```

`ReadSkillCatalog` is an injectable Location-scoped delegate to complete `SkillInfo`
values. The current adapter uses the existing Instructions/Skill producer pipeline,
including source precedence, frontmatter parsing and unavailable-source guards. It
does not create another Skill registry, fabricate bodies, resolve IDs as arbitrary
paths, or duplicate SkillSources parsing.

## Execution and Output

The leaf looks up the exact case-sensitive ID before permission. Missing IDs fail
with `Unable to load skill <id>`. Skill IDs are arbitrary source strings, not required
to have the legacy schema class's unused `skl_` prefix.

Authorization asserts `action: skill`, `resources: [skill.id]`, and `save: [skill.id]`
using the canonical invocation Session/Agent/Message/call context. No automatic
allow, external-directory grant, read permission, or autoinvoke shortcut is added.
Description-less or autoinvoke=false skills remain manually loadable if permission
allows them. Policy blocks and correction feedback become explicit recoverable
failures at this leaf boundary. User declines and interruption propagate unchanged.

The rendered content follows `Skill.toModelOutput`:

```text
<skill_content name="<name>">
# Skill: <name>

<trimmed SkillInfo.Content>

Base directory for this skill: <directory>
Relative paths in this skill (e.g., scripts/, reference/) are relative to this base directory.
Note: file list is sampled.

<skill_files>
<file><absolute resource path></file>
</skill_files>
</skill_content>
```

The exact rendered text is both the structured `output.output` string and the text
content part. Metadata is exactly `{ name, directory }`. Body content comes from
the catalog's parsed SkillInfo, not a second raw SKILL.md read that would reintroduce
frontmatter or bypass producer selection.

For a `SKILL.md` entry, the leaf samples the ordinally first ten resource paths
under its directory, includes hidden entries, excludes every basename `SKILL.md`,
and does not recurse into directory symlinks. Standalone root Markdown skills have
no resource sample. Only ten candidate file paths are retained; enumeration has an
explicit 100000-entry local limit rather than silently returning an incomplete sort.
This sampling limit is a bounded local extension. Resource files are not executed
or read for content by the skill tool.

## Session Callback Requirement

The source skill leaf does NOT call `SessionInstructions.load` or publish synthetic
instructions. It returns its skill content through the ordinary tool result. A
read-style callback would duplicate that content, invent an instruction dedup ledger,
and potentially commit success before output validation or after-execution hooks.

The required Session-owned callback therefore belongs AFTER snapshot execution:

```csharp
var result = await snapshot.ExecuteAsync(callName, callInput, context, ct);
await commitToolResult(callName, context, result, ct);
// Reload projected history for continuation only after durable result settlement.
```

`commitToolResult` represents the runner/Session owner's real ordinary tool-result
settlement API, not an implementation supplied by this file or a no-op callback.
It must commit the result against the actual active call ID, preserve canonical
content/output/metadata, and make that tool result model-visible in subsequent
history. Do not use inbox admission, API instruction entries, or a synthetic
`instruction.paths` message for skill activation. Repeated loads are legitimate
tool calls, not read-instruction dedup operations.

## Guidance Capability Handoff

The Instructions owner currently passes `canLoadSkills: false` to SkillGuidance.
That guard was not removed or bypassed. The runner must first advertise the real
captured skill definition, wire ordinary tool call/result persistence, and pass the
actual surviving request capability into instruction composition. The normal native
case checks the advertised definitions for `SkillTool.Name`, after catalog filtering
and request-level removals. Do not set this flag from config or catalog presence alone.
Continue using the existing SkillGuidance per-ID rules, descriptions and autoinvoke
filters; visibility is not execution authorization.

For full source lifecycle parity, the Skill owner should expose Location-scoped
`GetAsync(SkillId)`/`ListAsync` over its ordered producer registrations, plus reload
and skill-updated events. The current public API is a live local list observation;
the leaf uses it without pretending it is a persistent registry. Remote/plugin/
embedded discovery and source watcher lifecycle remain with that owner.

## Verification and Scope

Only Tools files changed. Core/Skill, instruction assembly, runner and Server were
not edited by the skill implementation. Foreground shell now has its own native
implementation and grammar limits; see `SHELL.md`.
Verification uses isolated Core builds only, with no skill/catalog/resource discovery,
filesystem inspection, provider calls, database access, tool execution or tests.
