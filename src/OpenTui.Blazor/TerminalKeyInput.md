# Rich terminal-key handoff

The existing Unix host wiring, reader, frame budgets, and dimensions are unchanged.
This change adds rich key delivery to the existing TerminalInput framer; it does
not install another escape interpreter or change native keyboard negotiation.

## Host callback

Add the optional named callback to the existing TerminalInput construction:

```csharp
richKey: input => pending.Enqueue(input)
```

It is a **replacement** for the legacy key callback when supplied. Keys are not
delivered twice. Paste, focus, mouse, capability replies, CPR precedence, byte
budgets, and input rejection retain their existing callbacks. A completed Unicode
scalar reaches the rich callback once, not once per surrogate. Windows Console
events retain their original ConsoleKeyInfo and do not pretend to have raw bytes,
lock state, or base-layout metadata that their current adapter did not supply.

Keep the whole TerminalKeyInput in the pending event queue. Do not immediately
convert it to ConsoleKeyInfo or throw away Raw/Sequence/Text distinctions.

## Matching and editing are separate

`input.TryGetKeymapEvent(out var match)` supplies the existing
`KeymapDispatcher.Dispatch(KeymapEvent, ...)` API with:

- normalized key name through the existing KeyStroke constructor;
- Ctrl, Shift, source Meta (Alt OR Kitty Meta), Super, and Hyper;
- Press or Release;
- Repeat as a separate boolean for Kitty event type 2;
- the source `baseCode` alternative, only when actually reported for a printable
  key. It is a Unicode base-layout codepoint, not a hardware scan code.

The current keymap implementation already keeps release matching separate and
only changes pending press sequences on press. The renderer/host must likewise
avoid editing, submission, text insertion, or compatibility key delivery for
release events. A release binding can be dispatched if registered; it must never
be downcast into a press.

Associated text and lock state remain on TerminalKeyInput; they are not shortcut
identity fields in KeymapEvent. Deliver the full Text as one committed text value
on the appropriate unhandled press path, respecting keymap preventDefault and
modifier/editor policy. Codepoint zero with associated text is exposed as
IsTextOnly; TryGetKeymapEvent returns false instead of inventing a physical key.
This is Kitty committed-text transport, **not full IME composition/preedit support**.

## Retained data

- Name, Sequence, Raw, Source, Code, Number, EventType, Repeated.
- Full modifier flags, retaining Alt separately from Kitty Meta. Option is Alt;
  Meta follows source `alt || meta`; Super/Hyper never become Alt.
- CapsLock/NumLock and HasReportedLockState. Raw/Console sources do not report
  lock state; false convenience values there are not physical-state evidence.
- Reported primary, shifted and base-layout codepoints; source BaseCode matching
  field; wire key/modifier/event numbers; associated-text presence and codepoints.
- AssociatedText separately from effective Text and its source (associated,
  shifted codepoint, explicit-Shift casing fallback, keypad, or primary codepoint).
- Alt-prefix information and observed ConsoleKeyInfo where actually available.

No Shift or base-layout code is inferred from text casing. The source's explicit
Shift fallback may uppercase generated text when the terminal omitted a shifted
codepoint; this does not change the reported identity or modifier bits.

## Protocol coverage

The Kitty table is exposed as TerminalKeyInputParser.KittyFunctionalKeys:

| Codes | Source meanings |
| --- | --- |
| 9, 13, 27, 127 | Tab, Return, Escape, Backspace aliases |
| 57344–57363 | Standard navigation, locks, print screen, pause, menu |
| 57364–57398 | F1–F35 |
| 57399–57408 | Keypad digits |
| 57409–57427 | Keypad punctuation/navigation and clear |
| 57428–57440 | Media and volume keys |
| 57441–57454 | Left/right modifiers and ISO level-shift keys |

CSI-u primary/shifted/base fields, modifiers/event subfields, associated scalar
lists, and source functional/tilde event forms are decoded. Event 1 is press,
2 is press+Repeated, and 3 is release. Omitted fields retain source defaults;
unknown event values and reserved modifier bits are rejected, not made into
presses. Source functional `R` remains unmapped because of CPR/F3 ambiguity;
unambiguous F3 CSI-u and supported legacy forms remain available.

modifyOtherKeys and legacy VT modifiers also retain Super/Hyper when reported.
Unknown private functional codes, malformed Unicode scalars, extra subfields,
and unsupported numeric syntax are explicit errors. Unlike the JavaScript
parser's permissive parseInt edge cases, malformed numeric suffixes are not
accepted. Runtime Unicode casing differences remain unverified; reported
codepoints and associated text are retained independently of that fallback.

## Legacy compatibility

`input.ProjectConsoleKeys()` returns Keys, explicit Loss flags, and
SuppressedRelease. It projects legacy behavior, not the complete wire record.
Loss reports repeat metadata, extended modifiers, active lock state, alternatives,
associated text, UTF-16 splitting, unavailable named-key identity, text-only input,
and the old Ctrl+J bridge for Linefeed. It does not turn distinct keypad navigation,
F25–F35, modifier-only or media identities into approximate Console key names.

Without a rich callback, existing raw/Console delivery remains available.
Enhanced sequences are only projected when compatible (or when splitting a
single Unicode scalar into the pre-existing UTF-16 editor path). Other metadata
requirements call inputRejected instead of silently flattening the key.
Releases produce no legacy key; SuppressedKeyReleaseCount and LastKeyInput expose
that compatibility decision. A rich consumer receives the original event instead.

## Negotiation gate and scope

NativeTerminal still calls SetKittyKeyboardFlags(..., 0). No extra event/reporting
flags were enabled. The generic owner must first consume rich events throughout
keymap dispatch, release filtering, text delivery and editing. Merely enqueuing
the new record is not sufficient to enable Kitty release or associated-text
reporting. No Host, renderer, Keymap or native negotiation files were changed.

Source comparison used installed OpenTUI 0.5.9 production parse.keypress-kitty,
parse.keypress and StdinParser code, plus @opentui/keymap normalizeKeyStroke,
OpenTUI press/release subscriptions, and addons/opentui/base-layout. The local
OpenTUI source checkout was also read; the installed 0.5.9 mapping is authoritative
where its newer keypad/space handling differs. Validation is compilation only;
no test sources, tests, keyboard/native/runtime probes, or IME execution were used.
