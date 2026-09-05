# Rich key routing integration

`OpenTuiHost` now supplies `TerminalInput.richKey` and keeps each complete
`TerminalKeyInput` in its event queue. The parser remains owned by the input
implementation; no duplicate escape decoder was added.

## Root application hook

Override this new `ITerminalApp` method:

```csharp
KeymapDispatchResult? DispatchKeymap(
    TerminalKeyInput input, KeymapContext context, TimeSpan now)
```

Run the same existing focus, editor-measurement, modal-mode, and pending-hint
setup used by the Console overload. Then use `input.TryGetKeymapEvent(out var
event)` and dispatch that event through the **actual registered** keymap layers.
This retains release/press, Repeat, Super/Hyper, source Meta and reported baseCode
for command conditions. Text-only commits have no invented physical key event.

The default interface method is deliberately restricted to lossless legacy
press projection. Root applications that have not added this overload still get
ordinary Console/raw presses. Metadata-rich unsupported input does not become
an ordinary key behind their backs. The existing single-scalar UTF-16 and raw
Linefeed/Ctrl+J bridges remain explicit compatibility paths.

## Editing

`Input.OnTextInput` receives `TerminalTextInputEventArgs` with the original input
record and its complete `Text`. Use a single editor transaction to insert that
committed string. It is not paste and must not be split into invented key presses.
This callback runs only for unhandled presses with textual content and without
active Ctrl/Meta/Super/Hyper modifiers. The owner's handler may inspect all retained
metadata. Release events are never sent to text insertion, legacy key editing,
submission, or app HandleKey fallbacks.

The host does not infer that an old editor supports associated text, extended
modifiers, alternate codepoints or repeat metadata. Without a matching registered
command or rich-text consumer, those events report an input error. This keeps
enhanced input from corrupting a prompt while root integration is incomplete.

## Embedded terminal

The focused `EmbeddedTerminalState` receives the full rich record before app
bindings, except events its `DeferRichKey` predicate returns to the app. The CLI
pane requires that predicate so a configured leader and active leader sequence
remain authoritative even for rich keys.

Native encoding retains Press/Release/Repeat, Super, reported CapsLock/NumLock,
associated text and the unshifted codepoint. Hyper and independent Kitty Meta
have no distinct field in the verified native ABI and cause an explicit error;
they are never silently dropped or aliased to ordinary presses. The public
`SendKey(NativeEmbeddedKey)` API also permits a producer with complete native
key metadata to provide consumed modifiers and composition state.

No Kitty keyboard/reporting flags were enabled. Root keymap and editor callbacks
must be integrated before any negotiation change; this document does not claim
full IME preedit or runtime keyboard interoperability.
