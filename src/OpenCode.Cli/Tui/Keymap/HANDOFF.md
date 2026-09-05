# Keymap Integration Handoff

## Ownership

Only new files in `OpenTui.Blazor/Keymap` and `OpenCode.Cli/Tui/Keymap` are part of this change. No application, host, input, native, project, or existing editor files were edited. Nothing mounts an app or registers global shortcuts automatically.

The generic library has no OpenCode IDs, mode names, default chords, editor implementation, console input conversion, timers, DI registration, React/Solid runtime, or I/O. The CLI adapter owns the OpenCode defaults and mode convention. `DefaultBindings.All` contains the 233 source definitions, including `leader`, unbound commands and retained deprecated config IDs. A definition is not a command registration.

## Main Owner Wiring

1. Create one `TuiKeybindConfig` from `FromCliConfig(JsonElement)` or `Defaults()`, one `KeymapLayerRegistry`, one `TuiKeymapMode`, and one dispatcher from `config.CreateDispatcher()` per app lifetime.
2. Register real callbacks with `TuiKeymapLayer.Create`. Register only supported commands. Do not create successful no-op callbacks for every default. Keep the returned registration leases stable until their component or scope changes, then dispose them. Later registrations win equal-priority ties; higher numeric priorities win first. Dispose target layers before destroying their target.
3. Map the normalized native input to `KeymapEvent(new KeyStroke(name, ctrl, shift, meta, super, hyper), type, baseCode, repeat)`. Preserve all modifiers and press/release information. This subtree does not read a key or reinterpret `ConsoleKeyInfo`. Native event names must already use the OpenTUI vocabulary, such as `return`, `escape`, `pageup`, `pagedown`, and `space`. `meta` means Alt/Option, not Super. Uppercase spelling does not imply Shift.
4. Build a `KeymapContext` for the event with the actual focused target and its reference-identity ancestor path through the root. With no focus, supply the root in `FocusPath`. Apply `mode.Apply(context)` before dispatching. Layer, command and binding predicates are reevaluated against this state.
5. Call `dispatcher.Dispatch(event, registry, context, monotonicElapsed)` on the existing UI owner. Callbacks run synchronously during dispatch. Returning `false` rejects a callback so the next registration can handle it. Admit asynchronous app work in the callback using the existing owner mechanism and return `true`; do not use `async void`.
6. Honor `PreventDefault` and `StopPropagation` independently from `Handled`. A handled binding with `preventDefault: false` intentionally still reaches later host handlers. `fallthrough` controls later matching keymap bindings, not host propagation. `Actions` and `Action` report callbacks already accepted; do not execute them again. `Pending` is the current sequence display state.
7. Call `Expire(monotonicElapsed)` from the existing owner tick, or arrange an owner wakeup for `Deadline`, to remove a leader hint while idle. Dispatch also checks expiry. No background timer is created here. Dispose layer leases and discard dispatcher state during app teardown; clear pending before replacing config and rebuilding layers.

For programmatic command palette/slash dispatch, use `DispatchCommand(id, registry, context, input)`. It resolves only active, enabled callbacks. The invocation carries optional command input and has no synthetic native event. `TuiKeymapCommand` retains palette/slash metadata for the owner's command UI; it does not implement those UIs.

## Modal And Editor Focus

Push `mode.Push(TuiKeymapLayer.ModalMode)` for a modal and dispose that lease when it closes. Register its commands in a `modal` layer. The default `base` layers become inactive; explicitly `global` layers remain reachable. Nested mode leases can be removed independently. Modal is a mode requirement, not a guessed numeric priority or a global keyboard trap.

Register `TuiEditorKeymap.CreateCommands(callbacks, focusedEditor)` first, then `CreateBindings(config, focusedTextarea)`. The command predicate accepts any live focused editor, including single-line Input, while the binding predicate must accept only a live focused multiline textarea. This preserves the source distinction between programmatic editor commands and managed-textarea shortcuts. Map the `input.*` callbacks to the current editor's existing operations. These adapters neither edit text nor attach to a native buffer. Register these provider-level layers before component layers at the same priority, matching source registration order. Their focus predicates, not the OpenCode base mode, govern activation so a focused modal editor can also use them.

The main owner must prevent a handled editing stroke from executing both the managed callback and the old editor key mapper. Only suspend/replace the old mapping once the actual supported callbacks are wired; retain the existing text-insertion path for unmatched events.

## Preserve Existing Semantics

`app.exit` has source defaults `ctrl+c,ctrl+d,<leader>q`, but the library never turns these into unconditional quit behavior. Bind app exit only to the owner's existing guarded app-exit callback. Register the focused prompt command `prompt.clear` with the owner's selection/text conditions so it wins before exit. Preserve the current order: Ctrl+C clears selection, then clears nonempty input/draft, then cancels the active request, and only otherwise requests exit. Do not assign this policy to a generic host.

Bind `input.delete.word.backward` and `input.delete.word.forward` to the existing word-boundary/selection-aware editor operations. Keep selection removal first, preserve grapheme/cursor behavior, and do not reconstruct editor semantics from raw shortcut characters. The source backward bindings are `ctrl+w,ctrl+backspace,alt+backspace`; forward bindings are `alt+d,alt+delete,ctrl+delete`. Preserve the existing cancellation/exit fallback for Escape in owner callbacks, while pending-sequence Escape is consumed before any app callback.

## Source Details

- Sources read in full: `packages/tui/src/context/keymap.tsx` and `packages/tui/src/config/keybind.ts`. Protocol and algorithm reference: installed `@opentui/keymap` 0.5.9 `types.d.ts`, default parser, enabled fields, active-layer ordering, dispatcher, binding lookup, leader, escape/backspace and base-layout addons. Textarea fallback keys are from installed `@opentui/core` `defaultTextareaKeyBindings`.
- Config accepts strings, stroke objects, binding objects, arrays of those items, `false`, and the top-level literal `"none"`. Binding objects support `event`, `preventDefault`, `fallthrough`, and retained JSON attributes; config `cmd` never overrides the containing command ID. `desc` and `group` compile as metadata. Unknown command IDs fail explicitly. Unknown extension attributes are retained on `BindingSpec` but have no behavior unless an owner adds a compiler.
- Comma strings expand into alternatives. `gg` means two strokes, not one key. `<leader>q` is a token followed by `q`. Space is a literal space stroke in the source default parser, not an Emacs chord delimiter. With a `+` and whitespace, the source parser expects one chord. CLI string aliases are `enter`, `esc`, `pgdown`, and `pgup`; object stroke names are not alias-expanded.
- The first configured leader binding is the trigger and must resolve to one stroke. CLI timeout precedence is `leader.timeout`, then shipped `leader_timeout`, then 2000 ms. Leader timeout resets when pending advances or pops. Other sequences do not acquire a timeout. Release bindings are single-stroke only and do not advance or clear pending input. Repeats are presses with preserved repeat metadata.
- A pending miss clears the sequence and returns unhandled without trying the key again from root. Escape clears and Backspace pops pending on press, regardless of modifiers. Primary-layout matches outrank base-layout fallback even across layers. Dead or disabled commands do not arm a sequence.
- Two source behaviors are intentionally retained: an empty configured lookup (`false`/`none` included) can use an explicit command-local fallback binding, and managed textarea bindings prepend config to the native defaults rather than deleting native defaults. Thus `none` removes configured bindings, not an explicitly supplied fallback/native map. Do not silently promise stricter disabling semantics than the source implements.
- Same-layer executable exact/prefix ambiguities are rejected, as the default provider installs no disambiguation resolver. This port reports invalid config/registration as exceptions rather than logging and silently skipping malformed entries. Pattern capture, arbitrary addon compilers, intercept hooks and deferred disambiguation are not implemented; unsupported pattern/token syntax fails explicitly. No generic policy is guessed for these extension points.

Regenerate the table with `GenerateDefaults.ps1 -Source <path-to-packages/tui/src/config/keybind.ts>`. The extractor rejects unrecognized definition shapes rather than inventing defaults.

## Verification

Build-only verification uses the repository-local pinned .NET 11 SDK, local NuGet sources, and isolated artifacts at `C:/tmp/opencode/keymap-build-ses-faa7c794`. No test, application, terminal, native operation, process tool, database or network workflow was run. Main-owner integration and runtime behavior remain unverified until that owner wires the API.
