# Persistent terminal pane handoff

## Mount status

This subtree provides a real native-backed `TerminalPane` and persistent-PTY
controller. It does not edit the root app, create a terminal, or establish that
the pane is mounted. The root owner must supply a real existing persistent PTY
identity and mount it in the session frame.

```razor
<TerminalPane @key="pty.Id" PtyId="pty.Id" Client="SelectedSessionClient"
              Theme="ElevatedThemeTokens" DeferRichKey="DeferTerminalKey"
              Resizing="IsResizing" AutoFocus="FocusTerminalOnMount"
              OnAutoFocus="ConsumeTerminalAutoFocus"
              OnFocusRequest="RememberTerminalFocusAction"
              OnFocusChange="TerminalFocusChanged"
              OnDisconnect="FocusPrompt" CancellationToken="AppLifetimeToken" />
```

`DeferTerminalKey(TerminalKeyInput)` must return true for the actual configured
leader key **or while the real app keymap leader sequence is active**. Inspect
the rich key identity with `TryGetKeymapEvent`; do not project Super/Hyper/base
layout keys into ConsoleKeyInfo for this predicate. Other terminal-focused keys
go to the native emulator before application bindings. Escape/Ctrl+C therefore
reach the child when there is no active text selection or leader sequence.

`OnFocusRequest` receives a callable focus-and-interact action, then null on
disposal. `OnDisconnect` fires when the focused terminal disconnects. All pane
state, native calls, start/disposal, and callbacks must run on the renderer
dispatcher, as they do through the Razor component lifecycle.

The root owns persistent-terminal list/create/remove actions using the existing
Client APIs. This component attaches only to the supplied ID. Ordinary Location
PTYs have different replay/control semantics and are not substituted for a
persistent terminal. No ordinary-to-persistent fallback or extra OS shell exists.

## Replay and input contract

`PersistentTerminalController` follows `packages/tui/src/component/terminal-pane.tsx`:

1. Wait for the embedded native surface, load the canonical persistent snapshot,
   and synchronously resize the native emulator before writing checkpoint bytes.
2. Reapply the theme palette after the checkpoint; connect from the snapshot's
   byte tail with a fresh attachment ID, takeover requested, and framed input.
3. Process binary output, resize/checkpoint barriers, and replay-complete markers
   in order. Adjacent output items are coalesced without crossing a resize.
4. Reset via RIS before a resize checkpoint and reapply the palette afterward.
5. Queue user input until `replay_complete`; send type 0/1 interaction frames with
   big-endian uint16 columns/rows. The frame size comes from the actual outer
   pane, minus the source two-cell horizontal inset.
6. Treat `attached.role` and `controller_changed.attachmentID` as authoritative.
   Focus/resizing can request interaction but never sets controller ownership
   optimistically. Automatic viewport resize interaction occurs only for a
   restored controller.

The native resize is synchronous in this port, so the canonical/native size
barrier does not depend on a later Solid layout callback. Emulator dimensions
remain canonical even when the surrounding pane clips its visible area.

Input-protocol version 1 is required. Failure is displayed; there is no fake
ready state. WebSocket fragments are assembled before control decoding. Writes
are serialized. Detach cancels reads/writes and disposes the socket but does not
remove the server PTY. Replay processing yields between messages so the host can
paint dirty frames without a second render loop.

## Native surface and theme

`EmbeddedTerminalState`/`EmbeddedTerminal` in generic Blazor own no server, app,
or process policy. `NativeEmbeddedTerminal` owns the actual OpenTUI emulator.
Output bytes feed its VT stream; `Compose` paints its cells directly into the
native frame under ancestor scissors. No text stripping or ANSI-looking screen
substitute is used. The compositor is invalidated before painting because the
generic host clears its shared frame, unlike upstream's retained widget buffer.

Cursor position, wide-tail correction, shape, blink, and color come from the
native emulator. The application cursor appearance is restored when the embedded
terminal loses focus. Native mouse encoding takes precedence; when the child
does not request mouse events, local selection and wheel scrollback remain
available. Selection text comes from the emulator, not a fabricated transcript.

`TerminalPalette.Encode` uses the source ANSI 16-color mapping, including the
source theme's specified hue steps for ANSI blue/purple/cyan slots. OSC 4/10/11
palette bytes are written **to the emulator**, never directly to the host output.
Native-generated response bytes are exposed as `EmbeddedTerminalDataSource.Response`.
The persistent pane forwards only `Input`, matching upstream and avoiding duplicate
replies from a client replay emulator; an ordinary-PTY consumer would need its
own explicitly correct response-routing contract.

## ABI and validation

Inspected installed OpenTUI 0.5.9 `EmbeddedTerminal.d.ts`, `zig.d.ts`,
`chunk-bun-b0662dgp.js` and `index.bun.js`, plus native `lib.zig` and
`embedded-terminal/{main,compositor,ghostty}.zig` source. ABI details used:
u32 handles, u16 dimensions, explicit byte spans/lengths, 14-byte external cursor,
12-byte key options, and signed status/required-length output handling.

The wrappers include key/mouse/paste/focus encoding, response draining, scroll,
selection, cursor and composition. Buffers are pinned for each synchronous call;
native-owned temporary encoded data is copied by the ABI before it is freed.
No handles or borrowed byte pointers escape the owning object.

Root rich-key integration is documented in `OpenTui.Blazor/RICH-KEY-HOST.md`.
Kitty negotiation stays off. Independent Hyper/Kitty Meta cannot be represented
by this emulator's verified six-bit modifier ABI and are rejected visibly rather
than flattened. No runtime, native-DLL load, PTY/socket/API call, screenshot,
process, clipboard operation, or test was executed for verification.
