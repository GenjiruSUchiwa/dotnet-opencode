# Picker Integration

Namespace: `OpenCode.Cli.Tui.Dialogs`. Render one picker while its controller
open flag is true. All markup uses the existing Blazor terminal components.
No picker fetches a catalog, starts a process, or creates fallback catalog data.

- `ModelPicker`: supply `Models` (`IReadOnlyList<ModelInfo>`), `Providers`
  (`IReadOnlyList<ProviderInfo>`), `Current` (`ModelRef?`), and optionally
  `Provider` (`ProviderId?`). Models are grouped by provider, sorted by release,
  and filtered by name, provider, or ID. Deprecated models are omitted.
  Disabled models/providers remain labeled unavailable and cannot be selected.
- `AgentPicker`: supply `Agents` (`IReadOnlyList<AgentInfo>`) and `Current`
  (`AgentId`). Hidden and subagent-only agents are omitted.
- `VariantPicker`: supply `Model` (`ModelInfo`) and `Current` (`ModelRef?`).
  Choices are the supplied variants plus Default (no variant override).
  Values preserve provider, model, and variant identity; display identities
  use `provider/model#variant`, never `provider/model/variant`.
- All pickers accept `TerminalHeight`, `Size`, `Error`, `OnSelect`, and
  `OnClose`. Model and agent pickers also accept `Loading`.

Bind `OnSelect` to an awaitable controller operation. For an existing session,
the actual network methods are `SessionHttpClient.SwitchModelAsync(sessionId,
modelRef, cancellationToken)` and `SwitchAgentAsync(sessionId, agentId,
cancellationToken)`. Let failures propagate to the picker; do not swallow an
exception and return success. Update controller selection only after success.
`OnClose` should clear the controller's open flag. The picker invokes it after
successful selection or idle dismissal. Selection is singleflight, and dismiss
is blocked while it is pending. Exceptions remain displayed in the open picker
until a retry; controller load errors are shown through `Error`.

ModelPicker opens its variant stage before applying a model with variants, so
the controller receives one complete ModelRef and performs one switch, rather
than two requests with an intermediate variantless model. Escape from this
stage cancels the whole picker without applying a selection.

The app-local PickerDialog surface is necessary because the current generic
SelectDialog has no category or inline-error slots. It uses the shared Modal,
Box, Input, and TuiText APIs rather than changing those generic components.
Modal supplies widths 60/88/116; the existing layout engine caps these at the
terminal width minus two. Keyboard support includes arrows, Ctrl+P/N,
Home/End, PageUp/PageDown, Enter, Escape, Ctrl+C clear/close, Ctrl+U clear,
grapheme-aware editing, and paste. `>` indicates keyboard focus and `*` marks
the current value. Search results flatten provider headings.

Validation: attempted CLI-only build with `--no-restore
-p:BuildProjectReferences=false`. It was blocked by stale referenced assemblies
(including missing existing ModalSize and SessionHttpClient types) and missing
Markdig metadata in concurrent transcript work. No dependency build, test,
runtime, native, or API operation was run.
