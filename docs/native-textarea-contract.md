# Native textarea and routed-event checkpoint

## Delivered path

`Textarea(State=...)` is a native-owned editor, separate from legacy controlled
`Input`. It has no Value/cursor/selection pair requiring a second application
editor. It is application-neutral and introduces no package, asset or native ABI
change. Existing Input callers and their requested aliases remain compatible.

Call chain: Textarea → renderer binding to the dispatcher → NativeEditor
(EditBuffer + EditorView) → Yoga native-renderable measure target **2** →
bufferDrawEditorView → native visual cursor/selection. The new path never calls
MeasureInput, TerminalTextEditing.WordBoundary or application undo/caret logic.

## Native ABI and ownership

- Verified against pinned OpenTUI 0.5.9 native lib.zig/edit-buffer.zig/editor-view.zig
  and installed editor wrappers, not only the higher-level TypeScript signatures.
- createEditBuffer takes widthMethod **and event-sink handle**. This owner supplies
  sink 0; native internal cursor events still update EditorView. Managed observers
  run after owned operations return, never from an editor C event callback.
- EditBuffer/EditorView/borrowed TextBuffer are uint handles. Logical cursor output
  is three u32 fields; visual cursor output is five u32 fields. Pointer selection
  uses the verified packed flags byte: update-cursor bit 0, follow-cursor bit 1,
  selection behavior shifted by 2. No pointer/handle casts or new exports.
- Undo/redo use a nonzero 256-byte metadata output buffer, matching source; native
  maxLen=0 would skip the operation. The native rope owns text undo/redo history.
- EditorView is destroyed before EditBuffer. Borrowed TextBuffer is not freed
  separately. Styles, scratch coordinate view and native measurement leases have
  explicit lifetimes. Measurement leases defer physical editor destruction until
  the Yoga transaction detaches/frees its borrowed targets.
- Textarea owns State disposal on unmount/replacement. One State cannot mount in
  two components. Reparenting the same component retains the owner; a new Prompt
  mount needs a fresh State and a captured document. Cancellation failures still
  execute native/resource cleanup. Disposed state cannot be remounted or edited.

## Public editor operations

Await State.Ready or use OnReady before editing. All operations run on the bound
renderer dispatcher. The state constructor itself does not initialize native code.

| Surface | Contract |
| --- | --- |
| Text / Snapshot | Actual native text, UTF-16 cursor, native logical/visual cursor, native selection and revision. |
| SetText / Clear | Explicit full reset of native text/history and marks/payloads. Use SetDocument for a structured draft. |
| SetDocument / CaptureDocument | Text, cursor, selection extent/anchor, mark IDs/types/styles/virtual flags, portable UTF-16 mark positions and opaque application payloads. Validation occurs before replacing the document where the ABI permits it. |
| InsertText | Deletes selected text through native operations, then inserts through EditBuffer. No root-side insertion is required. |
| SetCursorUtf16 / SelectUtf16 / ClearSelection | Conversion and selection use native text primitives, not guessed cell widths. |
| Execute(command, select) | Native horizontal/visual vertical/word/line/visual-line/buffer movement, selection, select-all, deletion variants, newline, undo/redo and submit. |
| Bindings | Per-state KeyStroke → TextareaBinding map. Source defaults are installed; caller replacements/removals are explicit. |
| Focus / Blur / Disabled / Visible | Real focus ownership and cursor visibility. Hidden state does not occupy Yoga layout or paint; disabled state remains the same native document, not a TuiText replacement. |
| CreateMark / DeleteMark / InvalidateMarks | Existing managed virtual extmark rules, painted as native highlight ranges without replacing editor text or clearing native undo history. |

Native word boundaries and visual vertical movement are authoritative. Unshifted
edge/word motions collapse selection to the appropriate edge; shifted movement
keeps a fixed anchor. Ctrl+Shift+Left/Right are intentional user aliases for the
same native word-selection commands. Source defaults include Enter=newline and
Meta+Enter=submit; the Prompt adapter must map its configured submit/newline policy
through Bindings or routed handlers, not run another editing implementation.

Cursor conversion uses a scratch **native text view**, not a second editor: it
measures native display width plus native logical-line separators and validates
prefix selection. It has no cursor/edit history. Invalid UTF-16/native boundaries
are rejected rather than rounding into a surrogate or grapheme. Native selection
offsets include LF; highlight offsets exclude LF, so the paint adapter subtracts
the actual native prefix's line breaks before submitting native highlight ranges.

## Marks and typed attachment handoff

Marks use the existing TerminalTextMarks interval/virtual-motion rules. Native
text edits drive their display-coordinate adjustments. The companion history
stores only mark/payload snapshots; it is not a second text undo stack. Undo/redo
restores those alongside native text history. Opaque MarkData is retained by mark
ID and included in change/submit documents; the library never casts, serializes,
or converts application attachment/agent/skill types to strings.

Application payload objects must be immutable (or copied before giving them to
the editor). Create a placeholder with InsertText, then CreateMark with the native
UTF-16 endpoints, type/style and typed payload. Read event.Document.MarkData when
preparing admission. Register type names consistently when recreating a Prompt;
source integer type IDs are retained in snapshots. If directly editing Marks,
call InvalidateMarks; prefer the state methods so revision/paint remain coherent.

CaptureDocument supplies portable UTF-16 mark positions for width-method-aware
restore. A live width-method switch requires remounting with that document; the
editor does not silently replace native undo history. Draft persistence is an
application serialization responsibility with its own known payload types.

## Events, paste, focus and rendering

- OnKeyInput and OnPasteInput receive rich routed events and bubble through actual
  scene ancestors. PreventDefault and StopPropagation are independent. Existing
  pointer Handled=true is only a compatibility shorthand for both flags.
- Prevention must occur before the handler's first await. Default editing runs
  once during dispatch; an awaited handler cannot retroactively prevent it.
- The host offers global keymap policy first, then the native editor route. It
  does not project a routed textarea event back into ConsoleKeyInfo/root editing.
  Releases retain metadata for observers and never insert text. The keymap context
  exposes `terminal.textarea` for handlers that explicitly delegate to the owner.
- Unhandled modal Escape/empty Ctrl+C and focus-traversal Tab retain generic host
  behavior after routed handlers. Real editor commands take precedence over those
  defaults. Global selection/copy uses copied native selected text.
- Pointer selection uses native cell/word/line behavior (500 ms repeated-click
  window), followed by event/default autofocus ordering. Native wheel scrolling
  honors prevention and moves/clamps the cursor as the source does. Drag scrolling
  uses the source 0.2 margin and 16 rows/second via TimeProvider; refresh requests
  keep mouse selection following separate from keyboard caret following.
- Paste dispatch occurs even for empty text. The default path removes a leading
  decoded BOM, strips complete OSC/CSI using the pinned portable strip-ansi pattern,
  and applies the existing CRLF/CR/invalid-surrogate/control normalization and
  UTF-16 paste budget. A rich clipboard/attachment producer prevents the default
  synchronously, then performs its authorized asynchronous work.
- Content/cursor callbacks carry immutable Before/After snapshots, a complete
  document, mount cancellation token, and IsCurrent. Cursor notification precedes
  content notification after completed native operations/mark adjustments; stale
  notifications are suppressed when a synchronous callback replaces state.
  Managed notification batching is an explicit .NET adaptation, not exposure of
  every intermediate native event from within a C mutation.
- OnSubmit captures the full document synchronously. It does not clear input,
  admit a prompt, call a provider, or create a Session. The app owns those effects.
- Native placeholder styling, normal/focused text/background, cursor appearance,
  selection color overrides and virtual mark styles paint through EditorView.
  Transparent defaults and per-run native selection inversion remain unchanged.
  Ready/viewport observations are published outside native measurement/paint.

## Minimal Prompt adapter / removal gate

1. Create one TextareaState per Prompt lifetime and pass it to Textarea. Pass the
   source theme values and E2 width/min/max constraints, including minH=1 and the
   source max-height rule. No controlled Value echo loop.
2. OnReady, restore the captured typed document, then request focus when allowed.
3. OnContentChange, observe event.Document to update application prompt state and
   autocomplete; do not reinsert text or call SetText in response to the same edit.
4. Delegate editing bindings to Execute or use Bindings. Remove duplicate root
   caret, word, pointer-selection and text-undo implementations for this target.
5. OnSubmit, capture/use event.Document before an await. The app clears and admits
   once under its existing origin/session/cancellation rules, restoring only into
   the appropriate empty editor on failure. No admission code moved into the library.
6. Capture the document before route unmount, including mark payloads. Let Textarea
   dispose the owner. Legacy Input may remain for unrelated compatibility controls.

No CLI adapter was modified in this checkpoint. Translation must use the new owner
as one unit; merely putting Textarea over the old editing handlers is unsupported.

## Remaining limits and validation

No native/Yoga/editor/TUI, clipboard, parser/WASM, DI/SDK/database, provider/MCP,
PTY or test execution occurred. Source comparisons establish contracts, not
runtime ABI behavior, visual fidelity, Unicode/font outcomes or user acceptance.

Native mutation APIs are void and may swallow native allocation errors, as in the
source wrapper; there is no invented success/rollback status. Managed document
validation is not a native transactional guarantee. Coordinate snapshots copy text
and use native scratch views; Yoga still allocates per layout transaction. Both
are deliberate ownership-first tradeoffs, not performance improvements. Profile
only after explicit runtime authorization before optimizing or replacing flex.

The source Bun stripANSI fast path is not universally equated with the portable
pattern. The inherited one-second regex guard is framework-internal, not a
TimeProvider timer. Full IME/platform event cadence, scroll/reflow during selection,
cross-renderable editor selection, and source drag/drop event APIs remain to be
verified or separately completed; this is not an unrestricted source-parity claim.
The new state does not emit the source's raw event-bus callback stream from C.
The app's IME/admission scheduling must use committed content notifications rather
than arbitrary sleeps or a second insertion path.

Final compiler checkpoint is recorded after the final build below.

**Frozen checkpoint:** full CLI dependency build succeeded with **0 warnings and
0 errors** (29.78 seconds), using repository SDK
11.0.100-preview.7.26381.103 and `OpenApiGenerateDocuments=false`.
Artifacts: `C:\tmp\opencode\native-textarea-e6`.
Log: `C:\tmp\opencode\native-textarea-e6-build-final.log`.
No source changes after the final build. No tests/runtime probes or Git actions.
