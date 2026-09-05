# Native, transport, and clock source review

## Scope and verification boundary

Assigned C# surfaces: `src/OpenTui.Blazor`, `src/OpenTui.Native`,
`src/Transport.Pipelines`, and `src/Runtime.Time`. No project/package files,
generated files, assets/licenses/native/WASM binaries, application packages, ID
types, or global configuration were changed. No agents were delegated.

This is a source-level ownership/algorithm review, not proof of runtime parity.
The reference is installed OpenTUI 0.5.9 and its source identity
`df2fc1594bb7a1274fc490155305e3d9f61f1b01`. No application, parser, codec,
clipboard, terminal, Wasmtime/native/WASM, test, socket, or live-data execution
was used. Framework source inspection and compiler builds are the verification
boundary. There are no zero-copy or measured-performance claims.

## Implemented concrete fixes

### Retained segmented bytes and producer EOF

`SequenceBuffer.Release` previously advanced the examined position to the end
while retaining every byte. A subsequent `GetMemory` / producer EOF / `Commit(0)`
could leave retained content unavailable to `TryRead` because no new data was
published. Release now keeps the examined position at the retained start. Empty
`Consume(0)` is a no-op, and invalid consumption is rejected before changing the
reader. Public API and zero-backpressure Pipe options are unchanged, including
the parent's image collector sequence GetMemory / ReadAsync / Commit / ToArray.

### Partial text-record budgets

`TextRecordBuffer` now rechecks already-scanned characters against the current
limit before accepting a delimiter or EOF. Lowering the limit while a partial
record is buffered no longer allows the old larger record through merely because
the next input is a newline. UTF-16 units, CR/LF/CRLF handling and borrowed-memory
rules remain unchanged.

### Streaming syntax state and snapshot ownership

OpenTUI CodeRenderable resets `_hadInitialContent` when `streaming` changes
(installed `chunk-bun-jxfx3h5k.js`, streaming setter). CodeHighlightState now does
the same, so toggling streaming re-applies initial draw/hide policy rather than
retaining the previous stream's policy. A provider/filetype change clears stale
HasParser state until that producer returns a result. Syntax-rule collections
are snapshotted on update; caller mutation of a reused list can no longer alter
an outstanding request or hide a change from the equality comparison.

### CRLF mark coordinates

StringInfo treats CRLF as one text element. TerminalTextMap previously treated
only a standalone LF as a newline coordinate, yet subtracted LF counts for both.
With a native control-width callback returning zero, CRLF could therefore produce
a negative highlight offset. CRLF now contributes the same one newline coordinate
as LF before newline subtraction. Width of ordinary graphemes remains entirely
caller/native supplied; no guessed Unicode width table was added.

### WASM construction ownership

TreeSitterWasm now includes Store construction inside Engine cleanup ownership.
A failed Store constructor no longer leaves the already-created Engine without
disposal. Cleanup also disposes Engine if Store cleanup fails. Shared-memory,
table, relocations, pinned assets and export signatures are unchanged.

### Managed HTTP deadline clock

TreeSitterGrammarCache previously inherited HttpClient's system-clock 100-second
timeout. Its optional final `clock` parameter now controls an equivalent
per-request headers deadline through TimeProvider-backed CTS. HttpClient's own
timer is disabled. The timer is disposed at ResponseHeadersRead completion;
body reads retain caller cancellation rather than acquiring a new whole-body
deadline. The client is allocated only after constructor argument validation.
Offline packaging remains offline and does not start a timer or HTTP request.

## Reviewed families and retained contracts

| Family | Source/ownership checks and decision |
| --- | --- |
| Native FFI and colors | Renderer/text/image/emulator/syntax/clipboard declarations remain source-generated LibraryImport. OpenTUI object handles remain uint. OS handles/pointers remain distinct. RGBA8 channels remain low bytes of four ushort lanes, not values multiplied by 257. |
| Native image owners | Decode/retain/clone/resize/extract adoption, native failure statuses, dimension/byte limits, pixel copies and ICC lease release order reviewed. No inferred encoded-PNG getter or new graphics fallback. |
| Native text and syntax | View/buffer/style destruction order and external-style leases reviewed; native selection/display offsets remain distinct from UTF-16. No new managed word-wrap substitute. |
| Native clipboard | Serialized operations, cancellation/worker retirement, provider pumping, copied results, and service shutdown reviewed. Native status failures remain separate from empty data. No clipboard read occurred. |
| Host, Win/Unix input and pixels | Fixed console/native read APIs, parser lifetime after Unix reader shutdown, raw X10 path, paste rejection, rich release events, and coalesced pixel requests retained. No Kitty negotiation change or speculative pixel timeout. |
| Renderer/layout/selection | Retained view recreation by width method, syntax-document/style caches, pointer/selection routes, clipping and image/emulator integration reviewed. Display width remains native-authoritative. |
| Marks, forms, keymap, editing | Coordinate conversion, snapshots, pure host-driven leader deadlines, validation/editor/history roles reviewed. The CRLF correction above is implemented; application behavior was not copied into these libraries. |
| Code registry/query/WASM | Immutable registration snapshots, exact asset pins, offline cache misses, query/capture offsets, stale results and parser disposal reviewed. Existing source directive/regex and injection limitations remain explicit below. |
| Pipelines | SequenceBuffer, text records/chunks, bounded collection, socket message assembly and FrameConnection reviewed. Results crossing dispatch boundaries own bytes/text; cancellation and leaveOpen semantics retained. FrameConnection still discards failed pending writes on completion rather than reflushing them. |
| Runtime.Time | GetTimestampMilliseconds remains a transient monotonic coordinate, not a persisted timestamp. Linked CTS owns token registrations and the BCL provider timer. No mutable clock registry was introduced. |

Runtime.Time's infinite-delay timer behavior was checked against framework CTS
source: InitializeWithTimer creates the provider timer for a nonzero delay,
including InfiniteTimeSpan, so later CancelAfter uses that timer. No workaround
or new independently racing timeout was needed. Timings owned by the host and
clipboard pump already use supplied TimeProvider instances.

## Exact remaining gaps / parent handoff

- Runtime ABI loading, terminal rendering, Unicode edge cases, Win/Unix console
  behavior, image delivery, clipboard ownership, PTY replay and WASM parsing are
  **unverified** under the no-execution restriction. A source review/build cannot
  establish full visual or transport parity.
- .NET Regex's internal match timeout (form validation and Tree-sitter #match?)
  is not TimeProvider-injectable. The existing bounded regex calls remain; this
  pass does not silently remove their limits or replace the regex engine.
- Native clipboard operation deadlines and native parser/renderer internals use
  clocks behind their existing ABI. Managed polling uses TimeProvider; injecting
  native clocks would require coordinated native/API changes outside this scope.
- The new grammar-cache clock argument is optional and source-compatible. The
  integration owner passed `clock:` at the production highlighter's cache
  construction, sharing the same host clock. Production asset lookup stays offline.
- Code state remains dispatcher-owned. Grammar registration applies on the next
  highlight request; changing a registry does not itself request UI rerender.
- JavaScript regex semantics are not universally equivalent to .NET ECMAScript
  mode. Unsupported injection loading still falls back to plain source rather
  than reproducing the JS worker's partial-success logging. These existing limits
  are not represented as fixed by this pass.
- No parser/assets/package/license changes or scalar-ID API changes were needed.
  Hyper/independent Kitty Meta remain unsupported by the verified emulator ABI.

## Build and freeze

**FROZEN after successful compilation.** The full CLI dependency build completed
with 0 errors and 1066 warnings in concurrently owned application packages.
The four assigned libraries all compiled with **0 warnings and 0 errors**.
No external analyzer fixes were attempted.

Command (repository SDK 11.0.100-preview.7.26381.103):

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj `
  --artifacts-path C:\tmp\opencode\native-pass-45236fcd-7a1e-4c80-a416-e3da1e54759f `
  -p:OpenApiGenerateDocuments=false -v minimal
```

Elapsed: 1:07.59. Build output was inspected for diagnostics in OpenTui.Blazor,
OpenTui.Native, Transport.Pipelines and Runtime.Time; none were reported.
No tests were added, edited, or run. No further source changes after this build.
