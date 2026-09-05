# Full native TUI parity scope

The target is the complete production OpenCode V2 TUI, implemented with application
Razor components and the reusable Blazor/OpenTUI bridge. Matching the new-session
screen is not sufficient. The command palette and active-session screens are
explicit user-reported gaps and have priority.

The visual reference is `C:\Repos\sst\kind-nebula\packages\tui\src`.
The only intended visual addition is the agreed subtle blurple `dotnet` label above
the OpenCode wordmark. This is a port, not a redesign.

## Required surfaces

| Surface | Required behavior and presentation | Primary upstream reference |
| --- | --- | --- |
| Command palette | Real registered commands and availability; searchable settings; filtering, grouping, selected rows, shortcuts, scroll, dismissal, and focus restoration | `component/command-palette.tsx`, `context/keymap.tsx`, `ui/dialog-select.tsx` |
| Active-session frame | Correct transcript and composer geometry, session metadata, footer, wide sidebar, narrow overlay, pane layout and focus | `component/session-frame.tsx`, `routes/session/index.tsx`, `routes/session/sidebar.tsx` |
| Transcript | Streaming and completed messages, reasoning groups, exploration groups, tool-specific views, media, Markdown, code, tables, diffs, timestamps, cost, selection, copy, and message actions | `routes/session/index.tsx`, `routes/session/dialog-message.tsx` |
| Composer | Multiline editing, selection, undo/redo, history, submission timing, attachments, paste, slash/mention completion, shell mode, steer/queue controls, and pending-input presentation | `component/prompt/index.tsx`, `routes/session/composer/index.tsx` |
| Tabs and session management | Adaptive tab layout, overflow, active/busy/unread/attention states, navigation while execution continues, independent drafts and scroll, search, rename, delete, and scope persistence | `component/session-tabs.tsx`, `context/session-tabs.tsx`, `component/dialog-session-list.tsx` |
| Pickers and dialogs | Model/agent/variant selection, favorites and recents, valid variant retention, cycling, nested dialog stack, sizing, keyboard and pointer interaction | `component/dialog-model.tsx`, `component/dialog-agent.tsx`, `component/dialog-variant.tsx`, `ui/dialog.tsx` |
| Permissions and forms | Correct priority, real pending requests, once/always/reject, saved approvals, review/diffs, all form fields, validation, cancellation, and external-action completion | `routes/session/permission.tsx`, `routes/session/form.tsx`, `routes/home.tsx` |
| Auxiliary activity | Subagent, shell, and terminal views with real lifecycle, resizing, focus, status, and completion state | `routes/session/composer`, `component/session-frame.tsx` |
| Home | Source wordmark, responsive geometry, composer, integration status, footer, and global-form overlay | `routes/home.tsx`, `feature-plugins/home/footer.tsx` |
| Recovery | Separate connection, unavailable-location, input, execution, and rendering failures; appropriate retry/reload/reset actions without losing drafts | `component/reconnecting.tsx`, `component/error-component.tsx`, `routes/session/location-missing.tsx` |
| Theme and settings | Semantic roles, built-in light/dark modes, custom-theme fallback, contextual surfaces, settings persistence, and configured keybindings | `context/theme.tsx`, `theme`, `config`, `context/keymap.tsx` |
| Generic bridge | Native measurement and wrapping, layout/clipping/z-order, overlays, keyboard/paste/pointer input, hit testing, scrolling, selection, focus, and resource ownership | OpenTUI renderer and the production components that consume it |

## Integration rules

- A component is not complete until its production mount, state producer, and
  action callbacks exist. Missing callbacks must not simulate success.
- A generated keybinding definition is not an implemented command. Palette entries
  must reflect registered actions and their actual availability.
- Opening an existing or running session must load and observe its authoritative
  state. The UI must not depend on having submitted the prompt in that tab.
- Presentation state belongs to the correct tab/session. Navigation must not lose
  drafts, selected models, row expansion, pending requests, or scroll position.
- Use source breakpoints and pane widths. Do not reuse Home's 75-column cap for the
  whole active-session interface.
- Native capabilities belong in `OpenTui.Native`; reusable layout and interaction
  belong in `OpenTui.Blazor`; OpenCode state and views belong in `OpenCode.Cli/Tui`.
  The reusable bridge must not acquire OpenCode dependencies.
- Application views use `.razor`. Bun, SolidJS, and an ANSI imitation are not
  alternate implementations of this client.

## Evidence and current limits

This document defines scope, not completion. Source comparison must cover both
layout and state transitions, including narrow/wide terminals, empty/populated
sessions, streaming/idle execution, and nested modal states.

The current agreement permits builds only: no tests, application launches, native
execution, API calls, provider calls, database checks, or screenshot capture.
Successful compilation proves neither runtime behavior nor visual parity. Those
remain unverified until execution is separately authorized. Do not report the port
as visually or behaviorally 1:1 based on a build or on this inventory.
