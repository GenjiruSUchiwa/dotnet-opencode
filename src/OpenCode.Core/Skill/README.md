# Local Skill Sources

`SkillSources` observes complete `SkillInfo` values, including markdown content.
`SkillGuidance` supplies the explicit `core/skill-guidance` instruction source;
there is no instruction registry and no automatic tool registration.

## Source Mapping

- `config.ts` / `config/plugin/skill.ts`: global and farthest-to-nearest project
  `.claude/skills`, then `.agents/skills`, then global/project config `skill` and
  `skills` directories, followed by configured directory sources from every ordered
  document. Roots deduplicate in first-occurrence order. Relative configured skill
  paths use the Location directory; `~/` uses the global home, matching upstream.
- Discovery scans root `*.md` and recursive `SKILL.md`, includes dot files and
  follows directory/file links with cycle protection. Files sort ordinally within
  a source. Later files/sources replace earlier skills with the same ID.
- `config/plugin/skill-file.ts`: root standalone markdown uses its filename as ID;
  SKILL.md and nested skills use the parent directory name. Name defaults to ID.
  Description, slash, metadata `opencode/slash`, and `opencode/autoinvoke` follow
  the source's field rules. Metadata booleans may be booleans or trimmed true/false
  strings. Invalid frontmatter is skipped instead of inventing a skill definition.
- `config/markdown.ts`: YAML parsing uses YamlDotNet 18.1.0, including quoted,
  multiline, and nested metadata values, duplicate-key checks, and the upstream
  retry that rewrites unquoted top-level colon values as literal blocks. Literal
  blocks retain string typing rather than untyped scalar inference. This is not
  a claim of complete gray-matter/js-yaml dialect parity; no runtime fixtures were
  executed in this pass.
- `skill.ts` / `skill/instructions.ts`: guidance excludes denied skill IDs, absent
  descriptions, and autoinvoke=false. Canonical permission arrays concatenate all
  global rules before all selected-agent rules and reuse `PermissionRules.Evaluate`.
  Global legacy tool flags and permission maps use ConfigNormalize's action/effect
  migration order before native rules. Malformed policy fails rather than being
  silently ignored. Visibility checks do not grant approval: an ask rule may list
  a skill without authorizing a later skill-tool execution.
  Summaries sort by host locale and contain only id/name/description. Body-only
  edits are observed but do not fabricate a guidance delta.
- Initial, added, removed, and changed guidance wording matches upstream. Metadata
  changes restate the complete list; pure additions/removals use compact updates.
  Empty availability is an observed removal. Transient scan/read errors mark the
  source unavailable rather than deleting all previously loaded guidance; this is
  deliberately stricter than upstream plugin scan-error skipping.
  Discovery probes before file scanning propagate the same unavailable observation.
  A complete prior Location-owned document snapshot is retained; a first observation
  that cannot establish those documents blocks initialization.

## Limits

HTTP(S) skill sources still require the download/cache producer and fail explicitly.
The producer does not activate skills, publish skill events, or install filesystem
watchers. Visible/autoinvokable skills require a real captured skill definition before
guidance enters model context. The host-composed ToolLocationFactory now supplies
the real SkillTool, and the runner persists its ordinary tool result before reloading
history. No read-style synthetic or permanent instruction-path ledger is used for
skill activation. Text-only SDK embeddings without that composition still fail
preflight for visible skills rather than advertise a nonexistent tool. Denied,
description-less, and autoinvoke=false skills do not require guidance; manual calls
still use the leaf's real skill permission assertion.
