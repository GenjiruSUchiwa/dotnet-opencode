# Palette and selector integration

## Root-owner change required

Replace the root's `SelectDialog<string>` command list with:

```razor
<CommandPaletteDialog Commands="PaletteCommands" Context="PaletteContext"
                      Theme="DialogColors" TerminalHeight="_height"
                      Shortcut="AllShortcuts" OnClose="CloseDialog"
                      Settings="AvailableSettings" OnSetting="OpenSetting" />
```

- Capture the **pre-modal** keymap context when opening. Use
  `_keyLayers.ReachableCommands(PaletteContext)` for `PaletteCommands`. Keep
  reevaluating this against the captured context while the palette is visible.
- The inner generic `KeymapCommand` now exposes `Palette` (default false) and
  `Suggested: Func<KeymapContext, bool>?`, alongside its existing `Title`,
  `Description`, `Category`, and `Condition`. Set these on actual registrations.
  The CLI wrapper's older `TuiKeymapCommand.Palette` flag is not automatically
  copied to the inner command; the root's registration factory must set it.
- Set availability in the actual `Condition`, not just inside a callback that
  returns false. Do not enumerate `DefaultBindings.g.cs` to invent commands.
- `Shortcut` returns all configured display chords for one command ID. Empty
  means no shortcut; the palette does not invent one.
- `OnExecute` is optional. If omitted, the selected registration's real `Run`
  callback executes with the captured context after `OnClose`. If supplied, it
  receives the command ID **after close**, and the root must route it through
  its real dispatcher using the captured context, not stale modal focus.
- Settings are supplied as `PaletteSetting(Id, Title, Category, Keywords)`.
  They appear only during search and only when `OnSetting` is connected.
  `OnSetting` replaces the palette with the actual selected settings screen.
  There are no placeholder setting implementations.
- Pass resolved semantic colors through `DialogTheme`: text, subdued text,
  elevated background/backdrop, category heading, focused action background/text,
  selected formfield text, and focused input background/text. Do not use the
  compatibility fallback as a claim of full theme parity.

`CommandPaletteDialog` derives suggested copies, category order, search metadata,
and right-side shortcut/category hints from those registrations. It excludes its
own command. `DialogSelect<T>` implements the shared grouped/flat search view,
selection, current marker, pointer actions, wheel scrolling, and footer actions.
Existing `PickerDialog<T>` callers now use that view, so model/agent/variant
pickers receive the new rendering without a root change. Their old literal
`Error="Error"` forwarding has also been corrected.

`ModelPicker.Actions` accepts real `DialogSelectAction<ModelRef>` values for
provider/connect actions; no provider callback is fabricated. `DialogSelect`
accepts `ResolveCommand(ConsoleKeyInfo)` for configured modal action/navigation
bindings. Defaults retain arrow/Ctrl+P/N navigation, wrapping ten-item pages,
Home/End, Enter, and Tab/Shift+Tab footer action focus. `SectionNavigation`
adds Alt+Up/Down section movement. Escape is handled by the modal scope; Ctrl+C
clears a nonempty filter before a later Ctrl+C closes it.

For common dialog navigation, generic `DialogStack`/`DialogHost` provide
Push/Replace/Pop/Clear; `DialogHost` wraps **unwrapped dialog content**, so do not
nest a complete `DialogSelect` (which already mounts a Modal) inside it.

## Transcript pointer hook

Add `OnToggleRow="ToggleTranscriptRow"` to the mounted `SessionTranscript`.
It emits the same stable keys used by the existing keyboard row picker; no
second expansion state is introduced in the component. Drag releases do not
produce clicks.

## Source and limits

- `packages/tui/src/component/command-palette.tsx`: real registrations,
  suggestions, search-only settings, clear-before-run behavior.
- `packages/tui/src/ui/dialog-select.tsx`: quarter-screen dialog, four-cell
  header inset, category rows, selected/current styles, right-side hints,
  half-height row budget, selection scrolling, and action focus.
- `packages/tui/src/ui/dialog.tsx`: widths 60/88/116, terminal-width-minus-two
  clamp, z=3000 backdrop, focus restoration, close/pop distinction.
- Search scoring is ported from pinned fuzzysort 3.1.0 with its license retained.
  The implementation omits JS caches/heap optimizations. The accent folding
  covers the common Latin blocks, not every Unicode Script=Latin extension.
- The source does not highlight individual fuzzy-match substrings in its normal
  Option view; this implementation uses its selected-row highlight and bold
  title rather than inventing a new highlighted-substring style.

No runtime screenshots or behavioral verification were permitted in this pass.
