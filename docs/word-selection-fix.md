# Ctrl+Shift+Arrow word selection

## Source-confirmed cause

The inspected input path preserves Ctrl and Shift, but the CLI had no default
binding for Ctrl+Shift+Left or Ctrl+Shift+Right. Exact modifier matching therefore
returned no command. The unmatched Ctrl-modified key cannot become printable
prompt input, so the word-selection callback was not reached.

The pinned `@opentui/core` package is 0.5.9. Its textarea defaults and this checkout's
`packages/tui/src/config/keybind.ts` list Alt+Shift word selection, not Ctrl+Shift
arrows. This fix adds the user's requested conventional chord as aliases to the
existing `input.select.word.backward/forward` commands; it does not claim that the
pinned upstream configuration already listed them. No live user configuration was read.

## Complete production path inspected

1. `WindowsConsoleInput.Modifiers` retains SHIFT_PRESSED (16) and either control
   flag (4/8), and `TryRead` passes both into ConsoleKeyInfo. Repeated records reuse
   the same complete key. The Alt-numpad filter does not drop Ctrl+Shift arrows.
2. `TerminalInput.FeedCore` delivers decoded console keys through `EmitConsole`.
   `TerminalKeyInput.FromConsole` retains the original ConsoleKeyInfo and flags.
   The exact projection used by the CLI returns that original record.
3. VT `CSI 1;6 D/C` decodes modifier value 6 as Shift+Control. Kitty functional/
   CSI-u decoding also retains both flags; repeat uses the rich command path and
   release does not execute a press binding. Unix input feeds that same VT framer.
4. `OpenCodeApp.DispatchKeymap` maps the named LeftArrow/RightArrow to `left/right`
   with the original Ctrl/Shift flags. `KeymapDispatcher` matches the full stroke,
   not a modifier subset. No decoding or matching change was needed.
5. Both production Composer and SessionComposer provide an Input with FocusKey
   `prompt`, controlled cursor/selection anchor, and MaxHeight greater than one.
   The existing managed textarea layer is therefore eligible when that input is
   focused. Dialogs, permission forms, terminal panes, and sidebar overlays have
   their own focus/dispatch rules.
6. The resolved word-selection command calls the existing `ExecuteEditor` word
   boundary operation, then `MoveCursor(..., select: true)`. The anchor is created
   only once with `??=`. Repetition, reversing direction, reaching the anchor, and
   crossing it retain that anchor instead of starting a replacement selection.

## Changes

- `Tui/Keymap/TuiKeybindConfig.cs`: adds Ctrl+Shift+Right/Left only when resolving
  defaults for the existing word-selection command IDs. Explicit user values,
  including remapping or disabling, take precedence. Parsed shortcut descriptions
  and the normal prevent-default behavior use the same resolved configuration.
  No generated default file or native fallback table is changed.
- `Tui/Components/OpenCodeApp.Keybindings.cs`: separates plain word movement from
  word-selection handling. Plain Ctrl/Alt word movement collapses an existing
  selection to its corresponding edge, as pinned `moveWordForward/Backward` does,
  rather than advancing another word. Shifted word movement still extends from the
  active endpoint using the original anchor.

There is no second raw-key handler, synthetic key event, new key-name convention,
text replacement, or alternate selection renderer. No generic input-decoder or
rendering/color file was edited.

## Boundaries and runtime checks still needed

- The existing managed `TerminalTextEditing.WordBoundary` operates on UTF-16
  text-element boundaries and classifies Unicode whitespace, letters/digits/marks,
  and punctuation. It does not split a surrogate pair or combining text element.
  Soft wrapping does not enter that calculation; the native layout maps the
  resulting logical offsets to visual rows.
- Full native word-segmentation equivalence is **not established**: pinned OpenTUI
  delegates to EditBuffer's native word-boundary API, while this CLI currently uses
  the managed helper. This small routing fix does not replace that algorithm or
  invent a new word lexer. Punctuation, whitespace/newline transitions, emoji, CJK,
  and mixed scripts need comparison with the actual upstream runtime.
- Word selection sets `select=true`, so the existing attachment-mark movement path
  bypasses atomic snapping. Selection changes only cursor/anchor and render
  invalidation, not prompt text, payloads, metadata, mark IDs, or undo documents.
- Once runtime verification is authorized, check both Home and Session composers:
  left/right repeated selection, direction reversal across the anchor, extension
  of an existing pointer/keyboard selection, plain-word collapse, narrow wrapped
  input, multiline/Unicode text, and selection across attachment labels. Confirm
  raw Ctrl/Shift delivery in the user's terminal as well as the resulting visible
  native selection. User overrides and terminal-level shortcut interception can
  affect that delivery independently of the corrected default binding.

## Verification boundary

Source inspection and pinned repo-local .NET 11 full CLI build only, with isolated
artifacts and `OpenApiGenerateDocuments=false`. No tests were added, edited, or run.
No app/TUI/native/DI/WASM/clipboard/provider/model/DB/PTY execution, live config or
secret reads, Git operations, publication, or delegation were performed. The final
handoff records the actual build result. A successful build is not usability proof.

Full CLI dependency-graph build succeeded with **0 warnings and 0 errors**, using
the pinned SDK 11.0.100-preview.7.26381.103, offline restore, and
`OpenApiGenerateDocuments=false`. Artifacts:
`C:\tmp\opencode\word-selection-a9f6c081-3d68-47b5-812a-b4330dfec45d`.
Source/build edits are frozen for the parent handoff; runtime confirmation is pending.
