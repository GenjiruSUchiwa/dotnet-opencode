# Command Preparation

This subtree implements the built-in init/review commands, the local
ConfigCommandPlugin subset, and an adapter for the existing Location MCP prompt
map. It does not start a server, model, shell, MCP connection, or Session runner.
Server endpoints are composed by `CommandHostService`; this Core subtree owns no HTTP dependencies.

## API

- `CommandCatalog.LoadAsync(directory)` reads local configuration through the
  existing `ConfigLoader.LoadSnapshotAsync`, consuming document/directory sources
  in their actual interleaved precedence order. Claude/agents discovery entries
  do not contribute command definitions, matching ConfigCommandPlugin.
- `CommandDocuments.LoadAsync(documents, directories)` decodes a source document
  or scans a directory as the snapshot is folded. It does not reread configuration
  JSON or reconstruct source precedence from a lossy merged commands map.
- `CommandDocuments.ParseMarkdown(name, content, source)` decodes one command.
- `CommandCatalog.List()` and `Get(name)` return canonical `CommandInfo` metadata.
- `GetDefinition(name)` exposes the template, description, agent, model, subtask,
  and originating document/file without making these runtime details wire fields.
- `CommandCatalog.PrepareAsync` performs selection callbacks and template
  evaluation, returning a `PreparedCommand`, not an assistant response.
- `PreparedCommand.AdmitAsync` forwards the unprepared prompt and delivery mode
  to the supplied Session admission owner.
- `CommandRuntime` implements the ordered last-definition-wins callback map with
  `List`, `Get`, and `PrepareAsync`. `Local` adapts a local catalog; `Mcp` adapts an
  existing `McpObservation.Prompts` and `McpRuntime`, without opening connections.
- `BuiltinCommands.Definitions(projectDirectory)` returns native `init` and
  `review` callbacks backed by embedded prompt resources.
- `CommandRuntime.Create(projectDirectory, catalog, owner, prompts, mcp, plugins)`
  is the Location startup/reload composition function. It registers built-ins,
  then the shared MCP map, then optional external plugin definitions, then local
  config definitions. This is the pre/external/post order from the source plugin
  host. All producers use the same runtime map; config overrides both callbacks
  and metadata without moving an existing command's list position.

## Supported Sources

Global JSON/JSONC precede global Markdown; explicit and ancestor direct documents
follow; each ancestor `.opencode` directory contributes JSON/JSONC followed by
Markdown; virtual config is last. Commands replace whole definitions by name,
not individual fields. Unrelated names survive later documents. Both
`command/**/*.md` and `commands/**/*.md` are scanned, including hidden entries and
symlinks, in ordinal full-path order. Names retain nested path segments with `/`.
Invalid or unreadable Markdown is skipped, as in the source. Directory scans that
fail contribute no commands. Invalid JSON command shapes fail explicitly.

Native `commands` and legacy `command` are supported. Native entries replace
legacy entries in the same document. Legacy model/provider/variant conversion
follows `config/migrate.ts`; Markdown decodes the native command schema directly.
Frontmatter supports YAML mappings, quoted/folded/literal scalars, aliases and
merges, and the source's unquoted-colon retry. The trimmed Markdown body always
replaces a frontmatter template. `subtask` is validated and retained but has no
execution effect, matching the current ConfigCommandPlugin.

This is not a complete plugin host. `LoadAsync` rejects
configured/auto-discovered third-party plugins rather than
silently returning an apparently complete plugin catalog. Remote well-known
integration config and plugin lifecycle/reload subscriptions remain host-owned.
Do not publish `CommandCatalog.List()` as the full server catalog when other
command producers are enabled. Rebuild the runtime map on config, command-file,
or MCP-prompt changes. Compose transforms in the actual host registration order.

## Built-In Resources

`BuiltinPrompts.resx` embeds the full, unabridged contents of
`packages/core/src/plugin/command/initialize.txt` and `review.txt` from the
TypeScript checkout `C:/Repos/sst/kind-nebula`, observed 2026-08-31. Each resource
has its source path in a comment. Preserve source punctuation, LF, and the final
newline when updating these assets. SDK default `.resx` embedding puts them in
`OpenCode.Core.Commands.BuiltinPrompts.resources`; no project-file changes,
generated source, host template injection, or runtime filesystem lookup is needed.

The exact names/descriptions are `init` / `guided AGENTS.md setup` and `review` /
`review changes [commit|branch|pr], defaults to uncommitted`. Preparation replaces
the first `${path}` with `Location.project.directory`, not the nested working
directory. It trims input with ECMAScript whitespace rules and replaces every
`$ARGUMENTS` literally, preserving the template's own whitespace. It does not
expand `$1`, interpret replacement tokens in user input, or evaluate embedded
shell. Built-ins do not switch agent or model. All attachments and delivery pass
through unchanged to ordinary Session admission. The template's requests to run
tools are model instructions, not eager tool execution: the normal runner and
permission service authorize each eventual tool call. There is no permission
bypass or separate built-in shell callback.

## Session Integration

At Location startup and each source reload, use the existing observed MCP map and
its owning runtime, not a second connection or a fabricated empty observation:

```csharp
var commands = CommandRuntime.Create(projectDirectory, catalog, preparationOwner,
    mcpObservation.Prompts, sharedMcpRuntime);
```

Store that one composed snapshot in the Location owner. Both the list endpoint
and Session command endpoint use it: `commands.List()` and
`commands.PrepareAsync(name, invocation, ct)`, followed by
`prepared.AdmitAsync(sessionAdmission, ct)`. A host that already has an ordered
plugin transform registry can register `BuiltinCommands.Definitions`, `Mcp`, and
`Local` into that registry instead of creating a second one.

The protocol request contains command, text/files/agents/skills, and optional
delivery. Resolve the Session and its current Location first; omitted delivery is
`Steer`. Return 204 only after preparation and durable admission complete. Map
`CommandNotFoundException` and `CommandExecutionException` to the protocol errors;
wrap admission errors at this same command boundary. Preserve Session-not-found
handling at the endpoint's existing Session resolution boundary.

`SelectAgentAsync` must read the current Session, switch only when its agent
differs, and return that agent's resolved model. An explicit command model wins
over this agent model. Without a command agent, do not reset to the current
agent's default model. `SelectModelAsync` uses the real Session switch operation.
These changes precede shell evaluation and remain applied if evaluation fails,
matching the source. Neither callback fabricates an instruction list.

`InterpolateShellAsync` receives Session ID, Location directory, and only the
embedded source. The owner resolves the configured shell (`priority: config`),
uses its platform argument convention, ignores stdin, and returns UTF-8 combined
stdout/stderr. If the host requires permission, ask through its existing
Location/Session permission service inside this callback. Do not introduce a
blanket `command.execute` permission or use the Session shell endpoint, which has
different events and ownership. Interpolation has concurrency two and preserves
match order; shell output is not trimmed or recursively interpolated. Argument
substitution happens first, so embedded shell in user arguments is also evaluated.

The admission callback uses the existing `AdmitPromptAsync` preparation/admission path:
resolve URI attachments and skill IDs, retain agent attachments, durably enqueue, then wake
the Session execution owner. Do not insert command templates as system
instructions or append AGENTS.md manually. Commands have no independent
instruction inheritance mechanism in the source; normal Session admission and
runner instruction loading own it. This subtree never writes messages, inbox
rows, instruction state, or execution claims.

## Native host integration and remaining limits

`PermissionAwareCommandShell` is registered in the managed Server. It borrows the
same tool Location and runs `LocalShellPolicy` before `ShellProcessSource`. Shell
approval is Session/Agent-scoped, with no fabricated message/tool source. It reads
the complete capture bytes, not the tool preview, preserving empty/nonzero-exit
output and avoiding truncation markers in templates. No default timeout is added.
Builtin and MCP prompts are not shell-interpolated.

The native host deliberately applies its existing shell permission policy to
interpolation; upstream ConfigCommandPlugin itself calls AppProcess directly.
The existing scanner supports its declared shell grammar, not every shell syntax
or shell family. Unsupported constructs fail before execution rather than bypass
approval. The process source retains its terminal environment markers and file
capture behavior. Those are explicit native host differences, not full AppProcess
behavioral parity.

The host rebuilds a complete command snapshot per list/execute operation. It does
not yet install the source's config-file watchers/debounce or publish command.updated
when no operation is observing changes. MCP connection ownership remains shared.
Configured/discovered plugin commands still fail explicitly until a real plugin
runtime supplies them. Attachment preparation and subsequent provider lowering
retain the Session owner's documented unsupported-case errors.

MCP command names sanitize server and prompt separately to `[a-zA-Z0-9_-]`, joined
by `:`. Positional prompt arguments use the same quoted/image argument parser;
missing values are empty strings. Result message text blocks are joined with
newlines and trimmed; non-text blocks contribute empty strings. Runtime failures
propagate, never become an invented empty prompt catalog or successful response.

## Source References

- `packages/core/src/command.ts`
- `packages/core/src/config.ts`
- `packages/core/src/config/plugin/command.ts`
- `packages/core/src/config/markdown.ts`
- `packages/core/src/plugin/command.ts`
- `packages/core/src/plugin/command/initialize.txt` and `review.txt`
- `packages/core/src/plugin/internal.ts` (pre/post registration order)
- `packages/core/src/config/normalize.ts`
- `packages/core/src/v1/config/migrate.ts`
- `packages/schema/src/config/command.ts` and `config/model.ts`
- `packages/protocol/src/groups/session.ts` (`session.command`)
