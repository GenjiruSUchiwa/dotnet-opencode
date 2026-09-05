# .NET Tree-sitter WASM host

## Integration

Explicit remote/local registrations and an integrity-checked query/grammar cache
are now available. See `GRAMMAR-REGISTRY.md`; this does not add remote defaults.
The CLI transcript separately selects its packaged production manifest through
one leased client; see `OpenCode.Cli/Tui/Transcript/GrammarAssets/README.md`.

`TuiCode` now uses a real `TreeSitterHighlighter` by default when Filetype is set.
The existing Transcript fenced-code hooks therefore reach the parser without a
root-owned producer assignment. An explicitly supplied `ICodeHighlighter` still
takes precedence and remains caller-owned.

**Root theme handoff:** continue passing
`SyntaxRules="@(TranscriptSyntax.Rules(themeTokens))"` to SessionTranscript (or
MarkdownText). The parser discovers captures; it does not invent theme colors.
An empty rule list produces no syntax colors, even when parsing succeeds. Root
markup and ModelPicker were not edited in this batch.

Mounted default code views lease one reusable parser client. It owns a serialized
Wasmtime store and per-language parser/query caches, not one engine per fence.
The final lease drains and disposes it. Compilation and parsing run on a managed
worker task, never on the renderer dispatcher. No engine is created by a static
initializer or by a build task.

## Implemented

- Exact Wasmtime `[44.0.0]` package reference; no JavaScript runtime or subprocess.
- Embedded, SHA-256-checked web-tree-sitter 0.25.10 runtime and OpenTUI 0.5.9
  JavaScript, TypeScript, Markdown, Markdown-inline, and Zig grammars/queries.
- Static dylink.0 memory/table allocation metadata reader; shared linear memory,
  shared indirect-function table, per-module base globals, runtime heap GOT entry,
  imported runtime function resolution, data relocations, and constructors.
- Explicit wasm32 addresses: 512 MiB maximum memory, 1,000,000 table entries,
  checked alignment/size/narrowing; no Memory64 assumptions. General-purpose
  dynamic libraries/GOT symbols are rejected, not resolved from the host OS.
- UTF-16 code-unit input callbacks, parser/tree/query lifetimes, transfer-buffer
  node/capture marshalling, native WASM query evaluation and stable capture order.
- Actual query equality, matching and membership predicates; #set! properties;
  source-shaped node-type/fence-language injections and offset remapping.
- Capture scope/injection/conceal metadata goes through the existing CodeProjection
  to real native syntax styles and ranges. Grammar/query files are unchanged.
- Cancellation callbacks for parsing and querying, cancellation checks during
  managed capture traversal, and existing streaming coalescing/stale-result
  rejection. An interrupted parser resets before its next one-shot request.
- No WASI filesystem, environment, process, network, or descriptor access. The
  imported clock reads time; fd operations return BADF. Unknown imports fail.

Only parser/runtime grammars are WASM. UI, orchestration, predicates, bindings,
and application/business code remain C#/.NET/Razor.

## Source behavior and explicit limits

- Default instances retain bundled languages only. C#, Python, Bash, Rust and
  other remote descriptors require explicit registry/cache configuration and
  reviewed asset pins. There is no implicit fetch or guessed lexer fallback.
- This is the CodeRenderable one-shot/reusable-parser path, not the worker's
  incremental editor-buffer edit API.
- Injection routing follows the inspected OpenTUI worker: Markdown inline/table
  nodes and fenced-code language nodes. It does not recursively run injected
  injection queries or invent support for Neovim directives the worker ignores.
- Like web-tree-sitter/OpenTUI, unrecognized directives such as lua-match? and
  offset! do not become new text predicates. is?/is-not? do not filter the worker's
  SimpleHighlight output. This is not a general Neovim query engine.
- #match? uses .NET's ECMAScript regex mode on Tree-sitter-selected node text only.
  The pinned query patterns were inspected; arbitrary JavaScript regex semantics
  are not claimed. A regex timeout is reported as failure, not empty captures.
- If a supported injection fails, the request reports failure and the Code state
  falls back to plain text; unlike the JS worker, partial injection failures are
  not yet logged-and-skipped. Unsupported injected languages produce warnings
  while retaining base captures.
- Cancellation does not interrupt Wasmtime module compilation or query creation;
  disposal waits for them. Parse/query callbacks and stale revisions cover model
  streaming changes without publishing obsolete results.
- Runtime execution, native loading, capture correctness, glyph rendering, and RID
  deployment have **not** been verified. A successful build is not a parity claim.

## Static verification and build

Read WASM type/import/export sections without loading Wasmtime. Confirmed callback
signatures, including WASI i64 arguments, query's eleven i32 arguments, five-i32
parse callback, and node/capture ABI against the pinned web binding. All twelve
bundled runtime/grammar/query hashes match the preceding proposal.

Build from the repository using its pinned .NET 11 SDK:

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj `
  --artifacts-path C:\tmp\opencode\syntax-wasmtime `
  -p:OpenApiGenerateDocuments=false -v minimal
```

No app, parser, native library, Wasmtime engine, or WASM module was instantiated
or executed during implementation/verification. No tests were added or run.
