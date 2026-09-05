# Pointer and overlay API

`Box`, `TuiText`, and `Input` expose typed callbacks:
`OnPointerDown`, `OnPointerUp`, `OnPointerMove`, `OnPointerEnter`,
`OnPointerLeave`, `OnClick`, and `OnWheel`.

`TerminalPointerEventArgs` carries zero-based screen/local cell coordinates,
button, Shift/Alt/Ctrl modifiers, wheel deltas, cancellation, and `Handled`.
Set `Handled` before the first await to stop bubbling/default scrolling.
For input callbacks, `TextIndex` is a UTF-16 grapheme boundary computed with the
same native-cell-width layout used for drawing. The controlled input owner must
apply that index to its cursor/selection state; clicking does not silently mutate
the owner's value. Input focus itself moves on left press.

Down/move/up callbacks use the pressed target while dragging. Clicks require a
left press/release on the same action without moving to another cell. Hit tests
respect ancestor clipping and reverse sibling paint order. The top modal blocks
underlying input. Wheel callbacks run first; otherwise the nearest scroll box
under the pointer scrolls three rows per vertical wheel step without changing
keyboard focus. Horizontal wheel deltas are available to callbacks and are not
silently converted to vertical movement. `PointerEvents="false"` makes a subtree
transparent to hit-testing. Window resize/focus loss clears pointer capture.

## Actual Windows input path

`OpenTuiHost` no longer polls `Console.KeyAvailable` or `Console.ReadKey`.
`WindowsConsoleInput` consumes `ReadConsoleInputW` records and retains both
KEY_EVENT and MOUSE_EVENT. It sets only ENABLE_MOUSE_INPUT and restores the saved
mode at shutdown; it does **not** enable VT input or change NativeTerminal.
It preserves .NET's modifier/lock-key filtering, Alt-numpad/IME key-up Unicode,
repeat counts, and decoded function keys. Mouse coordinates are translated from
buffer to window coordinates. Recognized VT mouse reports carried as characters
also pass through `TerminalInput` (SGR 1006, X10/1005, decimal urxvt 1015).
Pixel reporting is not negotiated. Paste remains opaque to escape decoding.

Source: dotnet/runtime `System.Console/src/System/ConsolePal.Windows.cs`,
`IsReadKeyEvent`, `KeyAvailable`, and `ReadKey`; Xterm ctlseqs Mouse Tracking.
These sources show why EnableMouse output plus Console.ReadKey was insufficient:
the .NET API consumes/discards non-key records. The new path avoids that loss.
This is source/build verification, not a claim of runtime-tested Windows input.

## Absolute positioning

```razor
<Box Position="TuiPosition.Absolute" Left="0" Right="0" Bottom="1"
     PaddingX="2" ZIndex="2000">
    @* Overlay content, measured at the available width. *@
</Box>
```

Absolute children do not reserve flex space. Offsets are terminal cells relative
to the containing box's content area. Opposing left/right or top/bottom offsets
stretch an unspecified dimension; otherwise natural/explicit size is used.
Ancestor clipping still applies. Siblings paint by ZIndex, retaining insertion
order for ties; hit-testing uses the same ordering. A Modal remains a separate
top focus/paint layer at z=3000. Absolute positioning alone does not trap focus.

`Box` can be keyboard focusable with `FocusKey` plus `OnKeyDown` without posing
as an editor. `TuiRenderer.Focus(key)` focuses a visible target in the active
modal scope. Existing input controls retain their editor traits.

`TerminalScrollState.Reveal(key, center)` resolves a keyed row after layout.
Selectors set `AutoFollow=false`; transcript scroll states retain bottom
stickiness by default. No rendering output uses raw ANSI; native OpenTUI still
owns painting, terminal modes, and frame output.
