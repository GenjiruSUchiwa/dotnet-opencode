# Agent Catalog

`AgentCatalog.ListAsync(directory, ct)` reads the native local catalog.
`ResolveAsync(directory, agentId, ct)` resolves an explicit ID, including hidden
agents, or selects the current default when the ID is omitted. Neither method
starts a model, registers tools, nor installs plugins.

## Source Mapping

- `packages/schema/src/agent.ts`: required request maps and base permissions.
- `packages/core/src/agent.ts`: ordered global directory grants and selectable
  default ordering. A configured hidden/subagent default falls back to Build,
  then the first selectable agent. Disabled and reintroduced agents retain the
  source Map's insertion semantics.
- `packages/core/src/plugin/agent.ts`: Build, General, Explore, Compaction, Title,
  and Summary metadata and ordered permission additions. `AgentPrompts.cs` copies
  the four prompt literals verbatim. Build and General have no separate system
  prompt in this source; the runner composes the harness prompt separately.
- `packages/core/src/config/plugin/agent.ts`: all document-global permissions
  precede per-agent transforms. Model references replace; request headers/body
  merge shallowly; metadata replaces only when present; permissions append.
  Only external_directory/read/edit resources expand home prefixes.
- `packages/core/src/config/normalize.ts`: legacy global tools/permission rules
  precede native permission arrays. Invalid input fails rather than providing a
  partial catalog with potentially missing deny rules.
- `packages/core/src/config/markdown.ts` and `config/plugin/agent.ts`:
  AgentDocuments parses YAML frontmatter with the source unquoted-colon retry,
  trims the body into system text, derives IDs from relative file paths, scans
  agent/agents recursively and mode/modes at top level, follows symlinks, and
  applies sorted agent files before sorted primary-mode files.
- `packages/core/src/v1/config/agent.ts` and `v1/config/migrate.ts`: legacy options
  and unknown fields enter request.body; sampling fields overlay those options;
  tools and permission maps retain precedence and action aliases. disable,
  maxSteps, theme colors, model/provider aliases, agent/mode maps and small_model
  are normalized into the canonical agent document shape.

The catalog reuses ConfigLoader and the internal ProducerConfiguration document
sequence. It does not flatten ordered agent transforms into one merged agents
object. Unavailable documents block the catalog instead of presenting retained
instruction snapshots as current policy.

## Boundaries

Plugin transforms remain unsupported and fail explicitly. There is no reload
watcher or mutable plugin registry: each call reads current source configuration. Catalog settings
do not imply that every runner feature, provider request override, subagent job,
or title/compaction workflow is implemented.

Invalid Markdown/YAML or frontmatter field types omit that file, following the
source parseOption/decodeOption behavior. Unreadable discovered files fail the
catalog instead of silently dropping policy. Legacy model references outside the
source provider/model syntax do not override model selection. No runtime plugin
or provider-option support is inferred from preserving their data in request.body.

## Instruction Handoff

`AgentDocuments.LoadAsync(documents, configurationDirectories, ct)` returns ordered
canonical document tuples. Global Markdown follows global JSON and precedes
explicit/direct files. Each project configuration directory's Markdown follows
its JSON; virtual content remains last. Native agent definitions replace migrated
definitions within one document, while AgentCatalog applies document transforms
in order. No configuration or instruction owner files are modified by this loader.

Readiness, the runner, and live permission rules should consume
`AgentCatalog.ResolveAsync` and its resolved AgentInfo. The existing LocalInstructions
path now does so, including the resolved permissions for skill guidance; it should
not independently reject agent directories or reparse a partial agent definition.

The permission Location factory can adapt its live resolver as:

```csharp
async (location, agent, ct) =>
    await AgentCatalog.ResolveAsync(location.Directory, agent, ct)
```

The owning factory must retain its real Location/project resolver and grant store.
Do not substitute a process-global allow policy. No additional catalog DI service
is needed. Server lists use CatalogLocation for canonical Location responses.

Verification is isolated compilation and static source review only. No runtime
catalog/config reads, tests, model/tool calls, database access, or commits were run.
