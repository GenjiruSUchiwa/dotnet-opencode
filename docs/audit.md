# OpenCode C# Port Audit Ledger

This document tracks the 1:1 faithful port of every file in the OpenCode repository.

## Codebase File Summary (3,549 Files)

| Package | Role | File Count | Port Status |
| :--- | :--- | :--- | :--- |
| `schema` | Data contracts & IDs | 98 | In Progress (10 / 98) |
| `protocol` | API routes & events | 45 | Queued |
| `server` | Kestrel Minimal APIs | 75 | Queued |
| `client` | HTTP & SSE client | 42 | Queued |
| `sdk` | Embedded host & SDK | 23 | In Progress |
| `core` | Domain execution engine | 693 | In Progress |
| `cli` | CLI commands & supervisor | 144 | In Progress |
| `tui` | Terminal User Interface | 374 | Queued |
| `app` / `ui` / `desktop` | Electron & Web UIs | 1,340 | Future Phase |
| Other packages | Merman, Latex, Theme, etc. | 715 | Future Phase |

## Tier 1: `packages/schema` (98 Files)

| TypeScript Source | C# Target | Status |
| :--- | :--- | :--- |
| `packages/schema/src/agent.ts` | `src/OpenCode.Schema/Agent.cs` | [x] Ported |
| `packages/schema/src/catalog.ts` | `src/OpenCode.Schema/catalog.cs` | [ ] Pending |
| `packages/schema/src/command.ts` | `src/OpenCode.Schema/Command.cs` | [x] Ported |
| `packages/schema/src/config.ts` | `src/OpenCode.Schema/Config.cs` | [x] Ported |
| `packages/schema/src/config/agent.ts` | `src/OpenCode.Schema/config/agent.cs` | [ ] Pending |
| `packages/schema/src/config/command.ts` | `src/OpenCode.Schema/config/command.cs` | [ ] Pending |
| `packages/schema/src/config/compaction.ts` | `src/OpenCode.Schema/config/compaction.cs` | [ ] Pending |
| `packages/schema/src/config/experimental.ts` | `src/OpenCode.Schema/config/experimental.cs` | [ ] Pending |
| `packages/schema/src/config/formatter.ts` | `src/OpenCode.Schema/config/formatter.cs` | [ ] Pending |
| `packages/schema/src/config/lsp.ts` | `src/OpenCode.Schema/config/lsp.cs` | [ ] Pending |
| `packages/schema/src/config/mcp.ts` | `src/OpenCode.Schema/config/mcp.cs` | [ ] Pending |
| `packages/schema/src/config/media.ts` | `src/OpenCode.Schema/config/media.cs` | [ ] Pending |
| `packages/schema/src/config/model.ts` | `src/OpenCode.Schema/config/model.cs` | [ ] Pending |
| `packages/schema/src/config/plugin.ts` | `src/OpenCode.Schema/config/plugin.cs` | [ ] Pending |
| `packages/schema/src/config/policy.ts` | `src/OpenCode.Schema/config/policy.cs` | [ ] Pending |
| `packages/schema/src/config/provider.ts` | `src/OpenCode.Schema/config/provider.cs` | [ ] Pending |
| `packages/schema/src/config/reference.ts` | `src/OpenCode.Schema/config/reference.cs` | [ ] Pending |
| `packages/schema/src/config/tool-output.ts` | `src/OpenCode.Schema/config/tool-output.cs` | [ ] Pending |
| `packages/schema/src/config/warming.ts` | `src/OpenCode.Schema/config/warming.cs` | [ ] Pending |
| `packages/schema/src/config/watcher.ts` | `src/OpenCode.Schema/config/watcher.cs` | [ ] Pending |
| `packages/schema/src/config/websearch.ts` | `src/OpenCode.Schema/config/websearch.cs` | [ ] Pending |
| `packages/schema/src/connection.ts` | `src/OpenCode.Schema/Connection.cs` | [x] Ported |
| `packages/schema/src/credential.ts` | `src/OpenCode.Schema/Credential.cs` | [x] Ported |
| `packages/schema/src/durable-event-manifest.ts` | `src/OpenCode.Schema/durable-event-manifest.cs` | [ ] Pending |
| `packages/schema/src/event-log.ts` | `src/OpenCode.Schema/event-log.cs` | [ ] Pending |
| `packages/schema/src/event-manifest.ts` | `src/OpenCode.Schema/event-manifest.cs` | [ ] Pending |
| `packages/schema/src/event.ts` | `src/OpenCode.Schema/Event.cs` | [x] Ported |
| `packages/schema/src/file-diff.ts` | `src/OpenCode.Schema/FileDiff.cs` | [x] Ported |
| `packages/schema/src/filesystem-v1.ts` | `src/OpenCode.Schema/filesystem-v1.cs` | [ ] Pending |
| `packages/schema/src/filesystem.ts` | `src/OpenCode.Schema/FileSystem.cs` | [x] Ported |
| `packages/schema/src/form.ts` | `src/OpenCode.Schema/Form.cs` | [x] Ported |
| `packages/schema/src/ide-event.ts` | `src/OpenCode.Schema/ide-event.cs` | [ ] Pending |
| `packages/schema/src/identifier.ts` | `src/OpenCode.Schema/Identifier.cs` | [x] Ported |
| `packages/schema/src/index.ts` | `src/OpenCode.Schema/index.cs` | [ ] Pending |
| `packages/schema/src/installation-event.ts` | `src/OpenCode.Schema/installation-event.cs` | [ ] Pending |
| `packages/schema/src/instruction-entry.ts` | `src/OpenCode.Schema/Instruction.cs (InstructionEntry)` | [x] Ported |
| `packages/schema/src/instruction.ts` | `src/OpenCode.Schema/Instruction.cs` | [x] Ported |
| `packages/schema/src/integration-id.ts` | `src/OpenCode.Schema/Integration.cs (IntegrationId)` | [x] Ported |
| `packages/schema/src/integration.ts` | `src/OpenCode.Schema/Integration.cs` | [x] Ported |
| `packages/schema/src/legacy-event.ts` | `src/OpenCode.Schema/legacy-event.cs` | [ ] Pending |
| `packages/schema/src/llm.ts` | `src/OpenCode.Schema/Llm.cs` | [x] Ported |
| `packages/schema/src/location.ts` | `src/OpenCode.Schema/Location.cs` | [x] Ported |
| `packages/schema/src/lsp-event.ts` | `src/OpenCode.Schema/lsp-event.cs` | [ ] Pending |
| `packages/schema/src/mcp-event.ts` | `src/OpenCode.Schema/ServerEvent.cs (McpStatusChanged)` | [x] Ported |
| `packages/schema/src/mcp.ts` | `src/OpenCode.Schema/Mcp.cs` | [x] Ported |
| `packages/schema/src/model.ts` | `src/OpenCode.Schema/Model.cs` | [x] Ported |
| `packages/schema/src/models-dev.ts` | `src/OpenCode.Schema/models-dev.cs` | [ ] Pending |
| `packages/schema/src/money.ts` | `src/OpenCode.Schema/Money.cs` | [x] Ported |
| `packages/schema/src/permission-saved.ts` | `src/OpenCode.Schema/PermissionSaved.cs` | [x] Ported |
| `packages/schema/src/permission-v1.ts` | `src/OpenCode.Schema/permission-v1.cs` | [ ] Pending |
| `packages/schema/src/permission.ts` | `src/OpenCode.Schema/Permission.cs` | [x] Ported |
| `packages/schema/src/persistent-pty.ts` | `src/OpenCode.Schema/persistent-pty.cs` | [ ] Pending |
| `packages/schema/src/plugin.ts` | `src/OpenCode.Schema/plugin.cs` | [ ] Pending |
| `packages/schema/src/project-id.ts` | `src/OpenCode.Schema/Identifiers.cs (ProjectId)` | [x] Ported |
| `packages/schema/src/project.ts` | `src/OpenCode.Schema/Project.cs` | [x] Ported |
| `packages/schema/src/prompt-input.ts` | `src/OpenCode.Schema/prompt-input.cs` | [ ] Pending |
| `packages/schema/src/prompt.ts` | `src/OpenCode.Schema/Prompt.cs` | [x] Ported |
| `packages/schema/src/provider.ts` | `src/OpenCode.Schema/Provider.cs` | [x] Ported |
| `packages/schema/src/pty-ticket.ts` | `src/OpenCode.Schema/pty-ticket.cs` | [ ] Pending |
| `packages/schema/src/pty.ts` | `src/OpenCode.Schema/Pty.cs` | [x] Ported |
| `packages/schema/src/question-v1.ts` | `src/OpenCode.Schema/question-v1.cs` | [ ] Pending |
| `packages/schema/src/question.ts` | `src/OpenCode.Schema/Question.cs` | [x] Ported |
| `packages/schema/src/reference.ts` | `src/OpenCode.Schema/Reference.cs` | [x] Ported |
| `packages/schema/src/schema.ts` | `src/OpenCode.Schema/schema.cs` | [ ] Pending |
| `packages/schema/src/server-event.ts` | `src/OpenCode.Schema/ServerEvent.cs` | [x] Ported |
| `packages/schema/src/session-compaction-event.ts` | `src/OpenCode.Schema/session-compaction-event.cs` | [ ] Pending |
| `packages/schema/src/session-error.ts` | `src/OpenCode.Schema/SessionError.cs` | [x] Ported |
| `packages/schema/src/session-event.ts` | `src/OpenCode.Schema/SessionEvent.cs` | [x] Ported |
| `packages/schema/src/session-fork.ts` | `src/OpenCode.Schema/SessionFork.cs` | [x] Ported |
| `packages/schema/src/session-id.ts` | `src/OpenCode.Schema/Identifiers.cs (SessionId)` | [x] Ported |
| `packages/schema/src/session-inbox.ts` | `src/OpenCode.Schema/SessionInbox.cs` | [x] Ported |
| `packages/schema/src/session-message.ts` | `src/OpenCode.Schema/SessionMessage.cs` | [x] Ported |
| `packages/schema/src/session-metadata.ts` | `src/OpenCode.Schema/session-metadata.cs` | [ ] Pending |
| `packages/schema/src/session-revert.ts` | `src/OpenCode.Schema/SessionRevert.cs` | [x] Ported |
| `packages/schema/src/session-stats.ts` | `src/OpenCode.Schema/SessionStats.cs` | [x] Ported |
| `packages/schema/src/session-status-event.ts` | `src/OpenCode.Schema/session-status-event.cs` | [ ] Pending |
| `packages/schema/src/session-transfer.ts` | `src/OpenCode.Schema/SessionTransfer.cs` | [x] Ported |
| `packages/schema/src/session-v1.ts` | `src/OpenCode.Schema/session-v1.cs` | [ ] Pending |
| `packages/schema/src/session.ts` | `src/OpenCode.Schema/Session.cs` | [x] Ported |
| `packages/schema/src/shell.ts` | `src/OpenCode.Schema/Shell.cs` | [x] Ported |
| `packages/schema/src/skill.ts` | `src/OpenCode.Schema/Skill.cs` | [x] Ported |
| `packages/schema/src/snapshot.ts` | `src/OpenCode.Schema/Snapshot.cs` | [x] Ported |
| `packages/schema/src/token-usage.ts` | `src/OpenCode.Schema/TokenUsage.cs` | [x] Ported |
| `packages/schema/src/tool.ts` | `src/OpenCode.Schema/Tool.cs` | [x] Ported |
| `packages/schema/src/tui-event.ts` | `src/OpenCode.Schema/tui-event.cs` | [ ] Pending |
| `packages/schema/src/v1/filesystem.ts` | `src/OpenCode.Schema/v1/filesystem.cs` | [ ] Pending |
| `packages/schema/src/v1/legacy-event.ts` | `src/OpenCode.Schema/v1/legacy-event.cs` | [ ] Pending |
| `packages/schema/src/v1/permission.ts` | `src/OpenCode.Schema/v1/permission.cs` | [ ] Pending |
| `packages/schema/src/v1/question.ts` | `src/OpenCode.Schema/v1/question.cs` | [ ] Pending |
| `packages/schema/src/v1/session.ts` | `src/OpenCode.Schema/v1/session.cs` | [ ] Pending |
| `packages/schema/src/vcs-event.ts` | `src/OpenCode.Schema/vcs-event.cs` | [ ] Pending |
| `packages/schema/src/vcs.ts` | `src/OpenCode.Schema/Vcs.cs` | [x] Ported |
| `packages/schema/src/websearch.ts` | `src/OpenCode.Schema/websearch.cs` | [ ] Pending |
| `packages/schema/src/workspace-event.ts` | `src/OpenCode.Schema/workspace-event.cs` | [ ] Pending |
| `packages/schema/src/workspace-id.ts` | `src/OpenCode.Schema/Workspace.cs (WorkspaceId)` | [x] Ported |
| `packages/schema/src/workspace.ts` | `src/OpenCode.Schema/Workspace.cs` | [x] Ported |
| `packages/schema/src/worktree-event.ts` | `src/OpenCode.Schema/worktree-event.cs` | [ ] Pending |
| `packages/schema/src/worktree.ts` | `src/OpenCode.Schema/Worktree.cs` | [x] Ported |
