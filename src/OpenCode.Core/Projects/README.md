# Project reads

`ProjectQueries(IDatabase).ListAsync` reads the host's existing `project` table,
ordered by descending time_updated then ascending ID. It maps the source
`project.ts:fromRow` fields, preserving canonical storage paths, timestamps,
sandboxes, optional names/icons/commands, and errors from invalid projections.
There is no second database or process-global project registry.

HTTP `GET /api/project` returns a bare `ProjectInfo[]`. `GET /api/project/current`
uses the existing `CatalogLocation` resolver and returns the bare current-project
shape `{id,directory,canonical}`. Client methods are `ListProjectsAsync` and
`CurrentProjectAsync`; neither wraps these in a Location/data envelope.

Source: `packages/core/src/project.ts`, `packages/protocol/src/groups/project.ts`,
and `packages/server/src/handlers/project.ts`.

`ProjectMutations.UpdateAsync` uses the existing transaction API and returns the
actual updated row. Omitted fields are unchanged; empty name/icon override/color
clear their stored columns, and empty commands.start clears commands. Every update
sets time_updated. Generated icon.url, canonical/VCS, initialization time and
sandboxes are not modified. The canonical `project.updated` event is ephemeral
and is published only after commit; no Session event or durable sequence is added.
HTTP PATCH returns the bare ProjectInfo and exact ProjectNotFoundError for a missing
ID. Client `UpdateProjectAsync` uses existing Schema icon/commands values.
Shared Server resolution now uses `ProjectDiscovery` for source durable worktree
announcements and Session project/subpath adoption; see `../Worktrees/README.md`.
The public CatalogLocation entrypoint now delegates to that same boundary; its
identity-only implementation is internal. SDK/direct Core and Server consumers no
longer diverge. The existing database/event files were not modified.
No tests, Git commands, database operations, or runtime requests were executed.
