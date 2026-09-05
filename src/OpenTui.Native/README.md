# OpenTui.Native

.NET bindings for the OpenTUI 0.5.9 native renderer. No Bun or Node runtime is required.
The binding is a subset of OpenTUI, not a port of its TypeScript input, layout, or component layers.

## Library deployment

Only the Windows x64 native binary is currently bundled. Supply the matching **0.5.9**
library for other platforms; library discovery does not download or build native assets.

Resolution order:

1. `OPENTUI_LIBRARY_PATH`, when set, must be the absolute path to a native library file.
   An invalid override fails rather than silently selecting another library.
2. Under the application directory, then the managed assembly directory, check
   `runtimes/<runtime-rid>/native`, `runtimes/<portable-rid>/native`, then the directory itself.
3. Fall back to .NET's native library resolution, including NuGet runtime assets.

Filenames are `opentui.dll`, `libopentui.dylib`, and `libopentui.so` on Windows,
macOS, and Linux respectively. Portable RIDs include `win-x64`, `osx-arm64`,
`linux-x64`, and `linux-musl-x64`. Use the architecture of the **process**, not the OS.
No user profile, package-manager cache, or working-directory search is performed explicitly.

Native assets retain their `runtimes/<rid>/native` paths when packaged.
`LICENSE.opentui` is the upstream MIT license and is included in build/publish output
and NuGet packages. Upstream: https://github.com/anomalyco/opentui (package version 0.5.9).

## Ownership

`NativeRenderer` owns one renderer and implements deterministic, idempotent disposal.
It validates dimensions and creation failure, rejects calls after disposal, and exposes
resize, buffer acquisition, and render operations. It does not configure OS input modes.
An optional native span feed is borrowed and must outlive the owner.

All calls and disposal must be serialized on the host's rendering thread. There is no
finalizer that changes terminal state on the GC thread; the host must use `using` or
`try/finally`. Raw `OpenTuiNative` APIs remain available for existing callers, but bypass
ownership checks. Never call `DestroyRenderer` on a renderer owned by `NativeRenderer`
or `NativeTerminal`.

Buffer handles are borrowed. Reacquire them after resizing and recalculate layout before
painting. Do not destroy renderer-owned buffers, free native framebuffer memory with a
managed allocator, or retain native memory views across resize/disposal.

`NativeTerminal` preserves the existing Windows console convenience API. It owns a
`NativeRenderer`, restores saved console modes on disposal or setup failure, and does
not restore modes again on repeated disposal. Its `Renderer` property returns zero after
disposal. It uses alternate-screen setup and disables Kitty negotiation for the existing
`Console.ReadKey` host. It does not set a terminal title; applications own branding and
can call `OpenTuiNative.SetTitle` explicitly. Other platforms need their own OS input-mode
host around `NativeRenderer`.

### Terminal lifecycle

Windows setup explicitly enables both processed output and VT processing. Input disables
line input, echo, processed input, Quick Edit, and VT input for the `Console.ReadKey`-based
host. Other saved input/output flags are preserved. Enabling extended flags is required
to change Quick Edit; restoration first applies the saved input flags with extended flags,
then restores the exact saved mode.

Native shutdown calls `destroyRenderer` before restoring OS modes. It does **not** call
`restoreTerminalModes`: upstream uses that function to re-enable TUI modes after focus
changes, not to tear them down. Native destruction uses `flushInput=false`, preserving
unconsumed input rather than choosing an application-specific discard policy.

Cleanup attempts native destruction and restoration of both console handles even when
native destruction fails. Win32 restoration failures are reported rather than silently
ignored. Multiple failures produce an `AggregateException`; failed construction preserves
both the setup and cleanup exceptions. Disposal remains one-shot even if cleanup fails.
No successful-path exception collection is allocated, and no finalizer mutates the terminal.

`NativeTerminal.Resize(width, height)` validates through its renderer owner and rejects
calls after disposal. Resize detection remains the host's responsibility; the wrapper does
not cache dimensions that could become stale after existing raw resize calls. Reacquire
buffers and recalculate layout after every successful resize call.

Cancellation is cooperative and belongs to the input/render loop. Processed input is
disabled, so Ctrl+C arrives as a key rather than reliably triggering `Console.CancelKeyPress`.
The application must map that key to its cancellation/exit policy. Stop and await input,
resize, and rendering work before disposing. Do not register `Dispose` directly on a
cancellation token: that callback can run on a different thread during a native call.
A token does not cancel an already-blocking `Console.ReadKey` or native render operation.
This layer adds no process signal handlers, exit hooks, or background reader threads.

Lifecycle references:

- [Microsoft SetConsoleMode](https://learn.microsoft.com/en-us/windows/console/setconsolemode)
  documents processed output, Quick Edit/extended flags, Ctrl+C, and resize input events.
- Installed OpenTUI 0.5.9 `chunk-bun-jxfx3h5k.js:8974-8993` re-enables terminal modes on
  focus; `9667-9785` stops input/scheduling before native destruction; `9355-9403` resizes
  and reacquires render buffers.

### Unix host blocker

Linux/macOS **NativeTerminal hosting remains unsupported**, independently of portable
native library discovery. No guessed `termios` struct or private .NET runtime imports were
introduced. Authoritative declarations were inspected:

- [glibc Linux termios types](https://github.com/bminor/glibc/blob/master/sysdeps/unix/sysv/linux/bits/termios.h)
  and [structure](https://github.com/bminor/glibc/blob/master/sysdeps/unix/sysv/linux/bits/termios-struct.h).
- [musl termios declarations](https://git.musl-libc.org/cgit/musl/tree/include/termios.h).
- [Apple termios definitions](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/termios.h).

Verified platform definitions alone do not make `cfmakeraw` a safe drop-in for the current
`Console.ReadKey` host. [.NET 10 pal_console.c](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_console.c)
maintains cached initial/current termios, reconfigures from that cache for console reads
and `KeyAvailable`, and restores/reapplies it during runtime lifecycle events. A separate
libc raw-mode owner would compete with that state, including signal-for-break behavior.

A portable implementation must first choose and coordinate one input/mode owner: a
dedicated byte-stream reader with verified per-platform terminal handling, or a design
coordinated with the public .NET Console behavior. It also needs resize and suspend/resume
handling. That host/input integration spans beyond this native-only change; merely removing
the platform guard would falsely advertise working Unix input. `NativeRenderer` remains
available for clients that supply their own platform-specific host and matching native asset.

## ABI and performance

- Renderer and optimized-buffer handles are `uint32`, even on x64. Actual native pointers
  use `IntPtr`/pointer types. Native Boolean arguments use one-byte marshalling.
- `NativeRgba` is 8 bytes: four `uint16` lanes with RGBA bytes in the low eight bits.
  Color intent occupies high bits; the current constructor produces explicit RGB colors.
- Text APIs take UTF-8 bytes and byte lengths, not UTF-16 lengths or NUL-terminated strings.
- `DrawText`, `MeasureCellWidth`, `SetTitle`, and `ProcessResponse` accept strings,
  `ReadOnlySpan<char>` slices, and `ReadOnlySpan<byte>` UTF-8 payloads. Existing string
  signatures remain available. UTF-16 wrappers share an encoder using a fixed 512-byte
  stack scratch buffer, falling back to a rented array for larger UTF-8 output. Byte-span
  overloads skip managed transcoding entirely.
- `MeasureCellWidth` uses native Unicode encoding and frees the returned allocation with
  `freeUnicode`. Pass the buffer's `GetBufferWidthMethod`: 0=wcwidth, 1=unicode, 3=unicode-wide.
- `Render` returns a status byte: 0=success, 1=skipped, 2=failed. Do not treat it as a Boolean
  or mark rejected frames as successfully painted.
- `DrawBox` binds upstream `bufferDrawBox` so a complete border can be drawn with one
  native crossing instead of allocating horizontal strings and drawing each vertical cell.
  Supply exactly 11 `uint` codepoints in this order: top-left, top-right, bottom-left,
  bottom-right, horizontal, vertical, top-T, bottom-T, left-T, right-T, cross. Options are
  left=1, bottom=2, right=4, top=8, fill=16; title alignment (0 left, 1 center, 2 right)
  is shifted by 5 and bottom-title alignment by 7. Titles are UTF-8 spans; their color
  defaults to the border color. There is no separate text-attribute parameter in this ABI.

Bindings were checked against the installed 0.5.9 `chunk-bun-b0662dgp.js` FFI table:
renderer/buffer APIs at lines 13697-13927, mouse/Kitty APIs at 14132-14154, Unicode APIs
at 14795-14801 and their ownership wrapper at 17158-17179. Platform filenames are at
8058-8062. Color packing is at 1031-1068. No ABI version probing is currently implemented;
deploy only a matching native library.

Box drawing uses the same FFI table at 14037-14054 and the upstream call wrapper at
15931-15938. The 11-codepoint layout is defined at 1388-1401; option packing at 11239-11265.

### Capability snapshots

`OpenTuiNative.GetTerminalCapabilities(uint renderer)` returns a `NativeTerminalCapabilities`
managed snapshot. The private source-generated binding is `getTerminalCapabilities(u32, ptr)
-> void`. Its naturally aligned structure follows upstream `chunk-bun-b0662dgp.js:13117-13147`:
20 byte fields, name pointer/u64 length, version pointer/u64 length, then two byte fields.
On x64 the name pointer is at offset 24, version pointer at 40, final fields at 56/57,
and total size is 64 bytes. The FFI declaration is at 14787-14789 and wrapper at 17121-17151.

Native-owned name/version strings are decoded and copied before returning; no borrowed
pointers escape. Serialize the entire snapshot operation with renderer mutation/disposal.
The snapshot allocates managed storage: request it after capability changes, not per frame.
After forwarding a recognized capability reply with `ProcessResponse`, refresh the snapshot
and force a repaint so negotiated rendering and width changes are visible while otherwise idle.

### Blazor input integration

`TerminalInput` keeps the existing three-callback constructor call source-compatible:
`TerminalInput(key, paste, response)`. The third callback now receives **recognized native
capability replies**, rather than every unrecognized escape sequence. Optional trailing
parameters are `Action<bool>? focus`, `Action<string>? unknownResponse`, and
`Func<bool>? cursorReportExpected`.

- `focus(true)` means focus-in; `focus(false)` means focus-out. The host can restore active
  native modes on focus-in after a blur using `RestoreTerminalModes`, as upstream does.
- `unknownResponse` receives complete unrecognized sequences, including unsupported mouse
  reports, theme/palette OSC replies, and pixel-size reports. Do not insert these into the prompt
  or indiscriminately forward them as native capability replies. Omitted callbacks discard them.
- `cursorReportExpected` disambiguates CSI cursor reports from modified F3 keys. Without it,
  a five-second startup query window is used. Hosts with later/repeated queries should supply
  their own query state. This is not a complete port of upstream's protocol context.
- The host's response callback should call `ProcessResponse`, optionally read capabilities,
  and request a forced frame. Declare captured repaint state before constructing the parser.
  This change does not modify the host callback wiring.

Bracketed paste is opaque until `ESC[201~`, including nested opening markers, arbitrary
escape sequences, Unicode UTF-16 units, and fragmented closing markers. Paste does not run
capability or key dispatch and is not flushed by escape timeouts. Its memory use remains
proportional to payload length because the existing API emits one complete string.

CSI/SS3/control-string buffering is bounded to 4096 UTF-16 units. Overflow or timeout enters
discard mode until the protocol terminates; it does not release the tail as keys. OSC ends at
BEL or ST; DCS/APC/PM/SOS end at ST. Classic X10 reports consume their three payload characters
without enabling mouse handling. The fallback supports navigation modifiers 1-8 (Shift/Alt/Ctrl),
Shift+Tab, and common SS3/CSI keys. Normal decoded `ConsoleKeyInfo` values pass through unchanged.
There is no whole-sequence string allocation per incoming character: keys parse spans, completed
response callbacks receive strings, and paste produces one final payload string.

Source references in installed OpenTUI 0.5.9:

- `chunk-bun-b0662dgp.js:6209-7689` contains the complete `stdin-parser.ts` implementation;
  `6448-6488` covers query context, `6775-7505` covers escape/control-string scanning,
  and `7617-7643` covers opaque paste with a retained delimiter tail.
- `chunk-bun-jxfx3h5k.js:6553-6584` defines capability/pixel-reply classifiers;
  `8941-9050` separates capability, focus, key, mouse, paste, and response dispatch.

The managed parser is a ConsoleKeyInfo fallback, **not** a byte-level UTF-8 decoder or a full
upstream parser port. Kitty input and mouse dispatch remain disabled. Its stricter overflow/
timeout discard-until-terminator policy intentionally prevents control-string tail leakage.

## Retained text views

`NativeTextView` owns one native text buffer and one associated view. Create it once per
retained text node, not once per frame. Native text, wrapping, virtual lines, and measurement
caches survive drawing. No buffer/view handles or native memory spans are exposed.

```csharp
using var text = new NativeTextView(widthMethod); // Match the renderer's negotiated width method.
text.SetWrapMode(NativeTextWrapMode.Word);
text.SetTabWidth(4);
text.SetStyle(NativeRgba.White, NativeRgba.Black, attributes: 0);
text.SetText("Retained content"); // Also accepts ReadOnlySpan<char> and ReadOnlySpan<byte> UTF-8.

var size = text.Measure(80); // Does not commit viewport geometry.
text.SetViewport(0, 0, 80, 20);
text.Draw(nextBuffer, x: 2, y: 3); // One native call; target handle is borrowed synchronously.
```

Update content only when it changes. `SetStyle` changes whole-buffer defaults without
uploading text; null resets the corresponding native default. Mark the host dirty after
content, style, viewport, or wrapping changes. The owner does not schedule frames or clear
the destination buffer. Existing buffer scissor/opacity state remains caller-owned.

### Ownership and ABI

There is **no `textBufferSetText` export** in this version. TypeScript `TextBuffer.setText`
uses retained memory registration, and `textBufferAppend` also retains caller memory.
Neither path is exposed by this abstraction. `SetText` submits one plain `StyledChunk`
through `textBufferSetStyledText`: verified native code copies its bytes into a reusable
native-owned allocation before returning. Plain `SetText` detaches any previously attached
syntax style so the plain chunk uses the buffer's defaults. Empty input clears both text
and highlights. The detached style remains owned for reuse by a later rich-text update.

The private chunk layout is pointer, `usize` byte length, optional foreground/background
pointers, `u32` attributes, optional link pointer, `usize` link byte length. On x64 its
offsets are 0/8/16/24/32/40/48 and its size is 56 bytes. All color/link fields are zero for
plain text. Source-generated P/Invoke signatures are:

```text
createTextBuffer(u8 widthMethod) -> u32
destroyTextBuffer(u32 buffer) -> void
createTextBufferView(u32 buffer) -> u32
destroyTextBufferView(u32 view) -> void
textBufferSetStyledText(u32 buffer, ptr chunks, u32 count) -> void
textBufferViewSetWrapMode(u32 view, u8 mode) -> void
textBufferViewSetWrapWidth(u32 view, u32 width) -> void
textBufferViewSetViewport(u32 view, u32 x, u32 y, u32 width, u32 height) -> void
textBufferViewSetViewportSize(u32 view, u32 width, u32 height) -> void
textBufferViewMeasureForDimensions(u32 view, u32 width, u32 height, ptr result) -> bool
bufferDrawTextBufferView(u32 targetBuffer, u32 view, i32 x, i32 y) -> void
```

Creation rejects zero handles and cleans up the buffer if view creation fails. Disposal
destroys the view, then its buffer, then any lazily created syntax style; buffer destruction
unregisters the style's destruction observer. Cleanup proceeds through nested `finally`
blocks and is idempotent. Calls after disposal are rejected. There is no finalizer and no native
terminal state mutation. Serialize all use/disposal; the owner is not independently thread-safe.
Renderer resize does not destroy this text owner, but callers must update view geometry and
reacquire renderer framebuffer handles. Recreate the text owner if its immutable width method
changes. Do not cache destination handles or raw framebuffer pointers in the text owner.

### Units and layout

- Text inputs are UTF-8 byte spans or UTF-16 strings/spans transcoded through existing stack/
  pool storage. Native-owned text survives input unpinning and pool return. Byte inputs must
  contain valid UTF-8. Text is not normalized with managed wrapping/tab substitution.
- `NativeTextWrapMode` values are `None=0`, `Character=1`, `Word=2`; initial native mode is None.
- `Measure(width, height=0)` returns an 8-byte `NativeTextMeasure`: `LineCount` and
  `WidthColumns`, both `uint`. Width zero or mode None measures intrinsic/max-content width.
  Height is currently ignored upstream, so it does not limit the reported line count.
  Measurement updates native measurement caches but does not commit wrap width or viewport.
- `SetWrapWidth(null)` or `SetWrapWidth(0)` removes the paint wrap budget. Measurement still
  uses its own supplied width. `SetViewport` and `Resize` set wrap width to viewport width.
- `SetViewport(x, y, width, height)` uses zero-based display columns and **virtual/wrapped
  rows**, not UTF-8 bytes, UTF-16 indices, or logical source-line indices. X scroll applies
  to unwrapped text. Width/height must be positive in this wrapper; skip drawing hidden nodes.
- `Resize(width, height)` preserves viewport scroll offsets. `GetVirtualLineCount()` returns
  the full virtual line count for current paint wrapping, not just visible rows.
- `SetFirstLineOffset(columns)` reduces only the first visual-line wrap budget, when the
  offset is positive and smaller than wrap width. It is not a text index or vertical scroll.
- Draw coordinates are signed, zero-based destination cells. Native clipping remains active.
- Native tab width defaults to 2, clamps to [2, 254], and rounds odd values upward. Use
  `SetTabWidth(4)` explicitly if replacing the previous managed four-space policy.

No text selection/range API is exposed here. Upstream's named "character" offsets are
display-width offsets that account for grapheme width, tabs, and line boundaries, not .NET
string indices. Do not add selection bindings by forwarding UTF-16 indices unchanged.

### Rich styled runs

`NativeTextView.SetStyledText(ReadOnlySpan<NativeTextRun>)` replaces the retained content with
generic styled runs. It performs no Markdown parsing or application-specific rendering.

```csharp
NativeTextRun[] runs =
[
    new("Important: ", Attributes: 1), // Bold
    new("read the "),
    new("documentation", Foreground: NativeRgba.Cyan, Attributes: 8,
        Link: "https://example.com/docs") // Underline + hyperlink
];
text.SetStyledText(runs);
// Measure, set viewport, and Draw as for plain text; do not resubmit unchanged runs per frame.
```

The primary run constructor accepts `ReadOnlyMemory<byte> Text`, optional `NativeRgba?`
foreground/background, a `uint Attributes` mask, and `ReadOnlyMemory<byte> Link`. Text/link
memory contains UTF-8; empty link memory means no hyperlink. The string convenience
constructor encodes and allocates UTF-8 arrays at run creation, not during subsequent draws.
Use memory slices when a parser already owns encoded content. Existing plain `SetText`
overloads and `SetStyle` remain available.

- Null run colors fall back to whole-buffer defaults; explicit colors preserve all four
  packed u16 lanes, including alpha and color-intent bits. Run attributes are OR'd with
  buffer default attributes, so a run with attributes zero does not clear default bold.
  Use default attributes zero when styling independently per run.
- Attributes use native base bits: bold=1, dim=2, italic=4, underline=8, blink=16,
  inverse=32, hidden=64, strikethrough=128. A nonempty Link is converted to a native link
  ID by upstream and replaces the run's link-ID bits. Do not fabricate IDs in upper bits.
- URLs are limited to 512 **UTF-8 bytes**, matching `LinkPool.MAX_URL_LENGTH`. The managed
  submission rejects oversized URLs rather than relying on native code silently dropping
  the link. No URL scheme validation or application link-opening policy is implemented.
- Place run boundaries on grapheme boundaries. Native code assigns highlights using
  display-width offsets; splitting a combining sequence between styles is not a .NET
  character-index operation. Runs are concatenated in order without inserted separators.
- A private syntax-style owner is created on first nonempty run-list submission, attached
  when required, and reused. `SetStyledText` with an empty list clears text/highlights and
  returns to plain mode. Lists containing only empty text also clear content natively.
- Plain `SetText` after rich content detaches the syntax style, clears old run highlights
  during replacement, and restores default-only styling. Returning to rich content reuses
  the style owner. No extra native style owner is allocated for plain-only views.

Batch marshalling has a fixed small-stack path: at most 8 chunk records, 16 packed colors,
and 1024 payload bytes. Larger batches rent chunk/color/payload arrays. Total lengths are
checked before packing. Text and URL bytes are coalesced into one payload, avoiding one
pin/handle per input run at the cost of a managed copy on updates. Only this staging batch
is pinned during marshalling/native submission. All rentals return in `finally`; used
chunk records are zeroed before returning them so pooled metadata keeps no call-scoped
addresses. Payload buffers are not securely erased, and pool misses can allocate.

No input run array, text memory, URL memory, color pointer, or chunk pointer is retained by
the managed owner. The caller must keep inputs stable during the synchronous call and may
release them immediately afterward. Native `setStyledText` copies text to its styled buffer,
copies optional colors/attributes into value-based style definitions, and copies URLs into
the native ref-counted link pool. It clears previous text-buffer link references on replacement;
framebuffers may independently retain references for already rendered cells. Do not free
URLs or link IDs manually. Native allocation failures still follow the existing void-setter
limitations below, including possible dropped styling/links rather than a managed error.

The existing naturally aligned 56-byte x64 StyledChunk layout is used unchanged; no Pack=1
or custom ABI was introduced. Additional source-generated exports are
`createSyntaxStyle() -> u32`, `destroySyntaxStyle(u32) -> void`, and
`textBufferSetSyntaxStyle(u32, u32) -> bool` (one-byte Boolean; zero style detaches).
The binding checks creation/attachment failures. Styles remain native-owned and are not
exposed as handles to callers.

### Limits and provenance

Text update is not incremental: each changed value is copied and parsed natively, while
unchanged text can be drawn repeatedly without managed encoding or wrapping. Native text
capacity and layout caches may retain their peak storage until disposal. `SetText(empty)`
clears content, not all retained allocations. Native syntax-style entries reuse names by run
index and can retain the peak run count. Public syntax-theme registration, editor operations,
selection, native Yoga integration, and append ownership are not implemented by this owner.

The upstream text setter is `void` and catches native errors; the wrapper cannot promise
atomic replacement or report all native allocation failures. `Measure` checks the native
Boolean result and throws on failure. Native drawing also has no success return status.

Installed 0.5.9 references: `chunk-bun-b0662dgp.js:11596-11632` shows the differing text
ownership paths, `13055-13078` defines the styled chunk, `14172-14406` defines FFI, and
`16442-16654` wraps text/view calls. `chunk-bun-jxfx3h5k.js:3140-3141` destroys view before buffer.

Native implementation was verified at the npm 0.5.9 package's recorded gitHead,
`df2fc1594bb7a1274fc490155305e3d9f61f1b01`, not an unpinned development branch:

- [lib.zig](https://github.com/anomalyco/opentui/blob/df2fc1594bb7a1274fc490155305e3d9f61f1b01/packages/native/src/lib.zig):
  exports at 2184-2425, 2506-2521, and 3220-3228 confirm ABI and failure behavior.
- [text-buffer.zig](https://github.com/anomalyco/opentui/blob/df2fc1594bb7a1274fc490155305e3d9f61f1b01/packages/native/src/text-buffer.zig):
  `StyledChunk`, `setStyledText`, `setDefaultFg/Bg/Attributes`, `setTabWidth`, and `getTextRange`
  confirm copying, default styling, tab rules, and offset units.
- [text-buffer-view.zig](https://github.com/anomalyco/opentui/blob/df2fc1594bb7a1274fc490155305e3d9f61f1b01/packages/native/src/text-buffer-view.zig):
  `deinit`, `setViewport`, `setViewportSize`, `getVirtualLines`, and `measureForDimensions`
  confirm lifetime, scrolling, and width-only measurement/cache semantics.
- [syntax-style.zig](https://github.com/anomalyco/opentui/blob/df2fc1594bb7a1274fc490155305e3d9f61f1b01/packages/native/src/syntax-style.zig):
  `registerStyleDefinition`/`putStyle` store value-based style definitions and copy style names;
  destruction notifies and releases observers.
- [link.zig](https://github.com/anomalyco/opentui/blob/df2fc1594bb7a1274fc490155305e3d9f61f1b01/packages/native/src/link.zig):
  `LinkPool.alloc` copies URLs into 512-byte-capacity slots; `LinkTracker.clear/deinit`
  releases text-buffer references. `lib.zig:3301-3309,3372-3386` defines style attachment/lifetime.
- [buffer.zig](https://github.com/anomalyco/opentui/blob/df2fc1594bb7a1274fc490155305e3d9f61f1b01/packages/native/src/buffer.zig):
  `drawTextBuffer` applies optional run colors and ORs run attributes with buffer defaults.

## Performance contracts

- All native entrypoints use source-generated `LibraryImport` P/Invoke, with explicit
  pointer/length arguments. There is no reflection, dynamic invocation, runtime string
  marshalling, or custom managed framebuffer copy in the drawing path.
- Span inputs are borrowed synchronously. UTF-8 arrays are pinned only for the native
  call. Stack/pool storage is never retained by these wrappers or exposed as a persistent
  native pointer. Do not return a caller-owned pooled array until the call completes.
- Rented UTF-8 buffers are returned in `finally`, including when encoding or the native
  call throws. Only the written prefix is passed to native code. The pool can allocate
  on a miss; it is not a zero-allocation guarantee for large text. Returned buffers are
  not securely erased. These APIs are not a secure-memory abstraction.
- UTF-16 uses the existing `Encoding.UTF8` replacement behavior for malformed surrogate
  sequences. Byte-span overloads expect UTF-8; they do not perform a second validation
  or normalization pass. Avoid slicing through a grapheme when measuring visual text.
- Native Unicode measurement still performs `encodeUnicode` plus `freeUnicode`, with
  an intervening scalar checked sum of cell widths. Removing a managed byte array does
  not remove the native allocation or the two native crossings. Cache stable measurements
  using text and the negotiated width method as keys, with a bounded cache lifetime.
- No custom SIMD kernel was added: encoding is delegated to the framework, and the
  current native-result summation is not an established bulk CPU bottleneck. No
  `SuppressGCTransition` attributes are used; native allocation, locking, terminal I/O,
  and rendering must retain normal GC transitions.
- Cache immutable UTF-8 text and border codepoints at the host/component level when
  reused. Use UTF-16 span overloads for substrings to avoid constructing a new string.
  Cached managed text is independent of renderer resize; framebuffer pointers are not.
- The native layer cannot infer whether raw drawing calls changed the frame. The host
  owns dirty tracking and should avoid layout/paint/render on clean frames. Keep dirty
  state after a skipped render and force repaint only when required, such as resize.
  Native terminal diffing does not eliminate managed work spent rebuilding the same frame.

Verification is build-only. Allocation and crossing reductions above follow from the
implementation; no throughput, latency, SIMD, or cross-platform performance claims have
been measured with benchmarks or runtime execution.

## Remaining integration work

The native DLL does not emit host key events. Full fidelity requires a managed streaming
parser for UTF-8, escape sequences, bracketed paste, mouse, Kitty keys, focus, and terminal
capability replies. Enabling mouse or Kitty through the new bindings is only appropriate
when the host can parse those protocols. Capability replies must be consumed and passed
to `ProcessResponse`, not inserted into the prompt.

The host must detect terminal resize, schedule rendering, restore OS input modes, and
handle cancellation. Public syntax-theme registration, editor/selection APIs, Yoga layout, Unix terminal
setup, and most other OpenTUI exports are not yet bound here.
No runtime validation of native loading, terminal restoration, or cross-platform behavior
was performed during this change; verification was limited to an isolated managed build.
