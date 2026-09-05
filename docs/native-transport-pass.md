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

## Pass 2 — managed query semantics, injections, scheduling, and HTTP cancellation

This pass resumes after the parent committed the first pass and its application
clock caller. Those changes are preserved. All implementation changes are in
OpenTui.Blazor C#; no application/package/asset/native ABI changes are included.

### Source evidence

- Installed `web-tree-sitter@0.25.10/tree-sitter.js`, lines 1235–1408:
  equality, matching, membership, property and custom-directive construction.
  Validation and regex construction happen during Query construction, not on
  the first matching node. Empty captures differ deliberately between equality,
  matching and membership. `not-any-of?` negates all-membership, not each member.
- Installed OpenTUI 0.5.9 `parser.worker.js`, lines 4025–4115: injection grouping,
  mapping precedence, node info strings, per-language load failure and per-node
  parse/query failure handling, container registration before parsing, and offset
  captures. Lines 4244–4292: nullish property fallback, first containing/contained
  range metadata, and stable start-index sorting.
- Installed `chunk-bun-jxfx3h5k.js`, lines 3211–3225, 3359–3380 and 3383–3488:
  content visibility during streaming, initial-content bookkeeping, no-capture
  plain-text behavior, stale snapshots, and separate loop/highlighting flags.
- dotnet/runtime v10.0.0 `System.Net.Http/HttpClient.cs`, HandleFailure,
  lines 577–614: caller-token attribution and timeout represented by
  TaskCanceledException with an inner TimeoutException. This source inspection
  did not make an HTTP request through the application or execute a parser.

### Implemented predicate/directive behavior

Query construction now eagerly validates arity and operand types for all source
text predicates: eq?, not-eq?, any-eq?, any-not-eq?, match?, not-match?,
any-match?, any-not-match?, any-of?, and not-any-of?. It compiles match patterns
even when the current document has no matching nodes. Invalid predicates can no
longer be hidden by an empty capture set or short-circuited previous predicate.

Set, asserted, and refuted properties are validated and stored separately.
Unrecognized directives retain their operator/operand arrays separately from
text predicates. The worker does not apply asserted/refuted properties as local
scope filters, nor execute custom directives such as lua-match?, offset!,
set-lang-from-info-string!, or arbitrary Neovim predicates. This implementation
does not invent such behavior. Source equality/membership quantifiers and their
empty-capture behavior remain in the evaluator.

### Explicit flagless-JavaScript regex subset

`TreeSitterPredicateRegex` translates actual query regexes; it is not a token
lexer or JS engine. Supported forms include literals, ordinary/noncapturing
groups, alternation, standard quantifiers, ordinary character ranges, escaped
punctuation, fixed x/u hexadecimal escapes, common control escapes, and positive
digit/word/whitespace classes. Outside classes, complemented digit/word/space
classes are also supported.

Concrete differences corrected:

- Flagless JavaScript `$` requires end of input; .NET's default `$` also matches
  before a final LF. It is translated to strict `\z`.
- JavaScript dot excludes CR, LF, U+2028 and U+2029; the translated class excludes
  all four rather than only LF.
- `\s`/`\S` use the ECMAScript whitespace and line-terminator set, including
  NBSP/BOM and Unicode spaces. `\w` and `\d` use explicit ASCII sets.
- `\xHH` consumes exactly two digits, rather than accepting .NET's longer
  hexadecimal escape. Matching remains UTF-16, like source RegExp without u/v.

Lookaround, named groups, backreferences/octal escapes, word-boundary assertions,
inline flags, non-fixed Unicode escapes, nested/empty character classes,
complement shorthands inside classes, shorthand range endpoints, and other
unimplemented escape forms fail with an explicit unsupported-construct diagnostic.
They are not silently evaluated with approximate semantics. These are deliberate
limitations versus universal JS RegExp. The unchanged one-second .NET regex
execution cap is still framework-internal and not TimeProvider-injectable.
No pattern or sample was executed to verify these translations.

### Injection results and lifetime

The managed highlighter now matches the worker's recovery boundaries: failure to
load one injected language or parse/query one injected node does not erase valid
base/sibling captures. Caller cancellation is rethrown, not turned into partial
success. Each created injection tree is freed in finally, including query errors.
Container ranges are recorded before parsing, as in the source. Built-in info
aliases retain source metadata (`js` → javascript; `jsx` → javascriptreact;
`ts` → typescript; `tsx` → typescriptreact; `md` → markdown). Empty mapping values
follow source truthiness/fallback rules.

Injected null-valued conceal properties now follow the worker's nullish fallback:
offset captures carry the query reference but not the original setProperties,
so null injected properties disappear while explicit empty-string properties
remain. Base-capture null properties retain the source's different behavior.

Partial output is **explicit**, not a full-success claim:
`CodeHighlightResult.IsPartial`, `CodeHighlightState.IsPartial`, and
`TuiCode.IsPartial` expose incomplete injection coverage with Warning/Diagnostic.
HasParser means the base parser loaded; it does not assert every injection worked.
The new init property preserves the existing three-argument constructor and
deconstruction shape. No parent caller change is required to forward results.
Parent UI may surface IsPartial/Diagnostic if desired; no parent markup was edited.

### Streaming/scheduling corrections

Initial streaming content is marked when parsing actually starts, not merely
when an update is queued. Subsequent streaming updates with DrawUnstyledText=true
publish the new plain content while work is pending; false retains the preceding
rendered document. The loop-active flag is separate from the public Highlighting
flag, so completion notifications observe highlighting=false unless a rerun is
pending. Cancellation/stale-revision checks remain serialized through the same
owned component lifecycle. Empty capture results without BaseHighlight now render
plain text as the source does, rather than applying a synthetic default style.

The component still does not expose source onHighlight/onChunks/initialStyledText
hooks. Recursive injection-query execution, automatic inheritance of Neovim query
files, and locals processing are not added; the inspected worker does not do
those in this path. No missing grammar/license file was created or substituted.

### HTTP deadline exception semantics

The prior TimeProvider deadline made HttpClient see our linked token as its caller.
It consequently returned linked-token cancellation without the original timeout
classification. GetHeadersAsync now attributes observable caller cancellation to
the actual caller token and wraps deadline expiry as TaskCanceledException with
an inner TimeoutException, preserving the framework distinction. Unrelated
cancellation is propagated unchanged. Caller cancellation takes priority when
observed; the timeout branch checks it again as the framework does. Exact localized
framework message text is not a compatibility promise.

The parent's clock argument remains effective. Deadline scope remains 100 seconds
per headers request, including each redirect; body reads remain caller-cancelled.
Offline mode still performs no network work. No transport/package/ABI change was
needed, and no application HTTP calls were used for verification.

### Pass 2 build checkpoint

Final build and freeze recorded after the last source change below. Verification
remains source/dependency inspection and the pinned repository SDK build only.

**Pass 2 FROZEN.** Final assigned-library dependency rebuild succeeded with
**0 warnings and 0 errors**, covering OpenTui.Blazor, OpenTui.Native,
Transport.Pipelines, and Runtime.Time (4.55 seconds).

```powershell
.\.dotnet\dotnet.exe build src\OpenTui.Blazor\OpenTui.Blazor.csproj -t:Rebuild `
  --artifacts-path C:\tmp\opencode\native-pass2-87036c36-5883-4f04-9c23-453d15029b3d `
  -p:OpenApiGenerateDocuments=false -v minimal
```

Full CLI build was also attempted after the last source edits. It is blocked by
outside-ownership CS0101/CS8863 in `OpenCode.Schema/SessionEvent.cs:58` (duplicate
`SessionIdleEventData`). An earlier full CLI pass succeeded with three external
Schema warnings, but it predates the final scheduling changes and is not claimed
as validation of the final full CLI state. Parent must resolve the duplicate and
repeat that build; no Schema source was edited here.

Logs: `C:\tmp\opencode\native-pass2-build-final.log` and
`C:\tmp\opencode\native-pass2-libraries-final.log`. No further C# source changes
after the successful owned-library rebuild. No tests, parser samples, WASM,
native, HTTP transport, or application execution occurred.
