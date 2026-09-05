# Configuration source snapshots

`ConfigLoader.LoadSnapshotAsync(directory, cancellationToken)` observes local
configuration sources once. It does not activate plugins, install watchers,
access credentials, create directories, or write configuration files.

`ConfigSnapshot.Sources` retains every accepted, substituted document as a
caller-owned JSON object, including fields outside the canonical wire schema.
`ConfigSnapshot.Merge()` applies the existing loader normalization and merge
rules to those observed documents. `ConfigSnapshot.Entries()` instead projects
each document separately into the existing Schema `ConfigEntry` union. It never
labels a merged configuration as a single source document.

`LoadDocument` shares discovery ordering but retains its original isolated-file
API, parsing/substitution behavior, strict failures, provider overlay rules,
atomic top-level replacement, and policy ordering. Other domains have not been
switched to a different configuration representation by this change.

## Local ordering

From lowest to highest priority, matching `packages/core/src/config.ts`:

1. Existing global/upward `.claude` roots, deduplicated within that group.
2. Existing global/upward `.agents` roots, deduplicated within that group.
3. Global `opencode.json`, then `opencode.jsonc`, then the global directory marker.
4. The explicit `OPENCODE_CONFIG` document.
5. Direct ancestor documents, filesystem root to the requested Location,
   `opencode.json` before `opencode.jsonc` at each level.
6. Ancestor `.opencode` documents and directory markers, root to Location.
7. `OPENCODE_CONFIG_CONTENT`, represented as a document without a path.

The existing global/XDG overrides and both existing project-disable flags remain
in effect. The project walk is disabled when the Location equals the global
configuration directory. Explicit documents are not deduplicated against
automatically discovered documents: repeated application is source behavior.
The global directory marker is a declared discovery root even if it has no
documents. Project ecosystem roots are only emitted when discovered.

## Wire contract and ownership handoff

`GET /api/config` returns **a bare array**, not `{ location, data }`:

- `{ "type": "document", "path"?: string, "info": OpenCodeConfiguration }`
- `{ "type": "directory", "path": string }`
- `{ "type": "claude", "path": string }`
- `{ "type": "agents", "path": string }`

The endpoint uses `RequestLocation.ResolveAsync` and the existing Schema types.
Client/Protocol owners should add a location-aware `config.get` returning
`IReadOnlyList<ConfigEntry>` and decode omitted virtual-document paths. There is
no Config.Entry `SourceStatus`, timestamp, content hash, status, or plugin-load
metadata. Plugin configuration in document `info.plugins` is a declaration,
not evidence that a plugin was loaded.

## Projection and remaining scope

The projection uses canonical Schema codecs, preserving native configuration
fields while rejecting malformed recognized values and reporting diagnostics
without their values. It handles the common legacy reference, command, agent,
permission/tool, plugin, provider/model, MCP, compaction, and policy mappings.
Native maps replace legacy entries within one document; permissions and plugin
declarations append in source order. Raw snapshot values are not pruned when
the canonical projection ignores unsupported or excess properties.

Special legacy provider transport migrations remain explicitly unsupported.
Remote credential-backed well-known configuration sources and their refresh
events are not implemented by this local snapshot boundary; no remote documents
are invented. Source `global:false` options, watch-driven persistent snapshots,
and credential-change reload integration remain service-owner work. Reads here
are fresh request snapshots, not a claim that config.updated events are wired.

The existing decoded-string variable-substitution behavior is preserved; the
upstream raw-text substitution implementation differs for unusual escaping and
comment cases. Complete malformed-V1 diagnostic equivalence and all legacy
validation edges still require authorized contract verification. No fidelity
claim is based on compilation alone.
