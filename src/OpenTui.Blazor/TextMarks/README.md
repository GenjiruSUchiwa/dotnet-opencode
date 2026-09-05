# Managed text marks / renderer contract

This is a managed port of installed OpenTUI 0.5.9's simulated
`ExtmarksController` and `ExtmarksHistory`. There is no native extmark FFI.
Only the existing caller-supplied native text widths and resolved `NativeRgba`
style values cross this boundary. There is no OpenCode Schema/Core dependency.

## Input renderer owner

Expose `Input.TextMarks: IReadOnlyList<TerminalTextMark>` and paint those spans
through the actual Input rendering path:

```csharp
TerminalTextMark(int Id, int Start, int End, TerminalTextMarkStyle Style, int Priority = 0)
TerminalTextMarkStyle(NativeRgba? Foreground = null, NativeRgba? Background = null, uint Attributes = 0)
```

`Start` and `End` are **UTF-16 indexes in Input.Value**, end exclusive. They are
paint-only; do not remove characters, replace a token with spaces, or move the
input cursor. Preserve mark identity, priority, bold/italic/underline attributes,
and inherited colors where a style field is null. Selection takes visual priority
over the mark. Do not stringify the range data into DOM-like attributes.

The application produces the spans from `TerminalTextMap.Project`. The renderer
must not treat these indexes as native text-buffer codepoint or display offsets.
If converting to another native API, use its actual index contract explicitly.

Initial/restored prompts may have marks before a keyboard event.
`TerminalTextMarkMetrics.FromInput(root, focusKey)` reads the actual width method
from an input that has completed layout. It supplies the existing native cell-width
measurement without changing renderer/Input ownership or adding an extmark FFI.
The root refreshes this measurement at its frame boundary; no keyboard event or
ASCII estimate is required. Before the first layout, marks wait for actual metrics.

## Managed editor owner

`TerminalExtmark` stores source **editor display positions**, with LF counting as
one position. `TerminalTextMarks` implements insertion gravity, all deletion overlap
branches, virtual cursor motion, exact-edge atomic backward/forward deletion,
type registration, lookup and snapshots. Clear retains the next mark identity,
like the installed controller. Snapshot restore also rebuilds the derived type
index so application lookups cannot retain deleted entries.

`TerminalTextMap` has explicit UTF-16, native-width display and Unicode-scalar
conversion methods. `HighlightOffsetAtUtf16` mirrors the source's
`offsetExcludingNewlines`; that display-minus-LF number is **not** renamed to a
codepoint count. Directional cursor movement uses source `start - 1` snapping,
clamped at the buffer start and converted to an actual grapheme boundary.

Selections bypass virtual cursor snapping. Backspace exactly at a virtual mark's
end and Delete exactly at its start remove the whole mark text. Range/selection
deletion is not expanded: partial overlap trims the interval and complete coverage
removes it. Inserting strictly inside a mark grows it; insertion at its start moves
it; insertion at its end leaves it unchanged.

The source distinguishes its wrapped EditBuffer offset setter from direct
EditorView/native positioning. Left/right apply that wrapped setter after finding
a virtual interval (including the source's second adjustment for adjacent/overlapping
marks). Visual up/down use their own nearest-edge rule. Line/buffer positioning and
pointer placement retain the unwrapped path; selection movement is never turned
into atomic mark navigation. These details were checked against
`chunk-bun-jxfx3h5k.js` EditBuffer/EditorView setters and textarea movement methods.

`TerminalTextMarksHistory` implements the source snapshot stack discipline. The
OpenCode root attaches the same mark snapshots and metadata bindings to its real
text-history snapshot identities, so text/marks/data restore in one transaction
instead of running two potentially misaligned undo stacks.

Source read in full: installed `lib/extmarks.d.ts`, `lib/extmarks-history.d.ts`,
and `chunk-bun-b0662dgp.js:9517–10237`. Application integration follows original
`component/prompt/index.tsx:765–879` and the existing prompt input producer.
Verification is build-only; no editor/native runtime or tests were executed.
