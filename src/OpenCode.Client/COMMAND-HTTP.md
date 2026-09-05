# Command HTTP client

`ListCommandsAsync(directory?, workspace?, ct)` calls `GET /api/command` and
returns the canonical `LocationResponse<IReadOnlyList<CommandInfo>>`. It preserves
server ordering and source descriptions; unavailable catalogs are errors, not empty lists.

`ExecuteCommandAsync(sessionId, command, PromptInput, delivery?, ct)` calls
`POST /api/session/{sessionID}/command`. The body contains command, text, optional
files/agents/skills, and optional queue/steer delivery. Omission uses source steer
behavior. It completes only on HTTP 204; it does not return assistant text or
automatically retry selection, interpolation, or admission side effects.

All fields use existing Schema values and source-generated Client metadata.
There is no Core/Server dependency. `SessionApiException` preserves command-not-found,
command-execution, and other actual error envelopes for the UI to display.

Source: `packages/protocol/src/groups/command.ts`, `groups/session.ts`,
`packages/server/src/handlers/command.ts`, and `handlers/session.ts`.
