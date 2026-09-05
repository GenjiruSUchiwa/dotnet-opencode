# Input mark rendering handoff

Bind the root's existing projected marks directly:

```razor
<Input Value="@PromptText" Cursor="Cursor" SelectionAnchor="SelectionAnchor"
       TextMarks="PromptTextMarks" />
```

`TextMarks` accepts `IReadOnlyList<TextMarks.TerminalTextMark>`. Its Start/End
are half-open UTF-16 indexes in the unchanged Input.Value. Foreground,
background, attributes, and priority are supplied by the caller. No theme or
OpenCode schema is read by the renderer.

The primitive caches an immutable paint projection and sends styled UTF-8 runs
to the existing retained NativeTextView. Ranges are clipped to current text and
snapped outward to whole text-element boundaries; no run cuts a surrogate or
combining sequence. The highest-priority covering mark supplies each span's
style, with stable input order for equal priorities. Unmarked spans inherit the
input's resolved defaults.

Input selection is applied through the same native view, using the existing
TerminalTextMap conversion with native element widths. Selection colors remain
caller-controlled. Cursor position, vertical viewport, and placeholder behavior
retain the existing input layout/focus boundary. Removing a mark removes its
style without removing/replacing its underlying text.

This is a paint hook only. The root-owned TextMarks controller still owns mark
identity, insertion/deletion adjustment, virtual-range navigation, metadata, and
undo/redo. No native extmark FFI or second edit/history controller was introduced.

Source comparison: OpenTUI's managed ExtmarksController and native
text-buffer.zig highlight-span priority selection. Verification was build-only;
no interactive/native input rendering or tests were run.
