# Native rich-text selection

This supersedes the plain-text-only limitation documented by the earlier
message-actions pass. `TuiText.Selectable` now supports plain text, styled runs,
and all three native wrap modes, including word wrapping.

## One mapping and paint engine

`TuiLayoutEngine.SelectionView` prepares the same retained `NativeTextView` used
for drawing. Its viewport remains relative to the whole text node; ancestor
scissors clip painting without changing the view's coordinate origin. Pointer
coordinates are passed to native local-selection APIs. There is no managed
word-wrap approximation and no UTF-16-to-cell index guess.

Native mapping handles tabs, combining marks, wide cells, consumed wrap
separators, logical newlines, and whole-grapheme endpoints. Selected text is
copied from the native text buffer. Styled-run colors, attributes, and hyperlink
metadata stay in the paint engine; the clipboard receives only selected source
text, not generated OSC/SGR sequences or hyperlink metadata. Soft wraps do not
introduce copied newlines.

Repeated clicks use the source 500 ms / one-cell policy: double-click selects a
native word, triple-click a native logical line. Native word boundaries are used,
not a second managed regular expression. A selection release does not activate
an underlying message/code action.

`NativeTextSelectionRange` contains half-open **display offsets**, not UTF-16
indexes or byte positions. The previous managed `Anchor`/`Focus` substring model
is no longer used. `TuiRenderer.Selection` exposes immutable native-selected
parts plus `Text` and `HasText`; each part records its native range and snapshot
position. Host Ctrl+C/clipboard behavior remains unchanged.

## Cross-block and scroll behavior

Selection starts in the text node's real container and expands through its
ancestors when the pointer leaves it, bounded by the active focus/modal scope.
Only selectable, positive-size nodes from the laid-out paint tree that intersect
the selection bounds participate. Collapsed transcript content is not in that
tree. No projected message history or hidden response text is concatenated.

Each touched native view computes its own selected range. Copied parts follow
OpenTUI's Selection.getSelectedText ordering: y then x, joining same-row pieces
and logical lines. As the scroll pane moves under a drag, the anchor stays
attached to its original scene node and native updateLocalSelection retains the
anchor display offset. Edge dragging scrolls the nearest source pane; selection
refresh runs after layout. Previously traversed offscreen rows between the
anchor and focus remain part of the scene-based selection.

After release, native display ranges survive width reflow and style-only updates.
A changed text snapshot, removed/zero-size node, or changed native width method
clears the selection rather than copying text using stale bounds. Native text
reads are bounded by the source buffer's reported byte size; returned lengths
are checked before UTF-8 decoding. Unchanged ranges reuse their copied text.

## ABI provenance

Verified against `@opentui/core@0.5.9`, whose npm metadata identifies git commit
`df2fc1594bb7a1274fc490155305e3d9f61f1b01`:

- `packages/core/src/zig.ts`: u32 handles, i32 local coordinates, optional RGBA
  pointers, u8 behavior, bool results, and u64 selection info.
- `packages/native/src/text-buffer-view.zig`: local mapping, inclusive cell
  occupancy, exclusive stored ranges, grapheme snapping, and plain-text copy.
- `packages/core/src/text-buffer-view.ts` and `lib/selection.ts`: retained local
  selection, container-relative anchors, touched views, and copy composition.

The packed selection sentinel is `ulong.MaxValue`; start is the upper u32 and
end is the lower u32. Colors use the existing four-u16 `NativeRgba` layout.
The wrapper does not expose native pointers or reinterpret offsets as managed
string indexes. No NativeTerminal or parser changes are part of this work.

Rendered Markdown inline runs, table cells, list markers, code, shell previews,
and expanded tool output now opt into this path. Validation remains source and
full-graph build only: no native rendering, clipboard calls, screenshots, or tests
were executed.
