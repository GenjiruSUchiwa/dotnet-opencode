# Tree-sitter parser host: original dependency proposal

**Status: approved and implemented as a build-validated first host.** Wasmtime
44.0.0 is now an exact NuGet reference, and TuiCode defaults to the real WASM
highlighter. See `TREE-SITTER.md` for the implementation and remaining limits.
The sections below preserve the original pre-approval assessment; statements
about no installed dependency describe that earlier batch, not current status.

## Delivered without a parser dependency

`TuiCode` and `CodeHighlightState` are implemented rendering/scheduling surfaces.
Native syntax-style registration, view leases, line/display highlight ranges,
capture projection, conceal metadata, source-line mapping, cancellation, and
stale-snapshot rejection are real code. No regex lexer, guessed token colors,
JavaScript engine, parser process, or parser dependency is installed.

Without an `ICodeHighlighter`, code remains plain and `HasParser` is false.
Fenced-code rendering now uses this primitive. Theme rules come from the existing
`ThemeSyntax.GenerateNative` through `TranscriptSyntax.Rules`.

## Proposed dependency

**Approve `Wasmtime` NuGet version `44.0.0` for a separate .NET Tree-sitter WASM
host.** It is published for .NET 8/9 and .NET Standard and is usable from .NET 11.
The package adds a native Wasmtime runtime; it is not a pure-managed or zero-native
dependency. Its package metadata reports Apache-2.0 WITH LLVM-exception and a
46.35 MB multi-target package. RID contents, package integrity, redistribution,
and deployment size must be pinned/reviewed before adoption.

Reference: https://www.nuget.org/packages/Wasmtime/44.0.0
Source: https://github.com/bytecodealliance/wasmtime-dotnet

The preferred architecture is a dedicated parser-host package implementing
`ICodeHighlighter`. Generic Blazor retains no application schema dependency and
no mandatory parser runtime. The host loads only approved, bundled grammar/query
assets; no runtime grammar download is enabled implicitly.

### Why this is not just `Instantiate(grammar.wasm)`

Static section inspection of installed OpenTUI 0.5.9 assets and
web-tree-sitter 0.25.10 found:

- All six modules contain `dylink.0`.
- Runtime and grammars import `env.memory`, `env.__indirect_function_table`,
  `__memory_base`, and `__table_base`; several also import `__stack_pointer`.
- The runtime imports `GOT.mem.__heap_base`, `emscripten_resize_heap`, abort,
  Tree-sitter parse/log/progress/query-progress callbacks, and limited WASI
  clock/fd functions.
- The Markdown grammar imports runtime allocation/string functions and
  `__assert_fail`; JavaScript/TypeScript import character-class functions.
- Runtime exports include `ts_init`, `ts_parser_new_wasm`,
  `ts_parser_parse_wasm`, node/tree cursor wrappers, `ts_query_new`,
  `ts_query_captures_wasm`, `ts_query_predicates_for_pattern`, and memory helpers.
- Each grammar exports `tree_sitter_<language>` and relocation helpers.

The host therefore needs a bounded shared-memory/table loader, dylink allocation
and relocations, exported symbol resolution, the web binding's transfer-buffer
ABI, UTF-16 source callbacks, parser/tree/query lifetime handling, and limited
imports. It must not grant filesystem/process/network access merely to satisfy
WASI imports. A proof against one pinned grammar must precede rollout.

### Query and worker behavior to preserve

OpenTUI CodeRenderable uses one-shot highlights with a reusable parser client.
The worker initializes `Parser`, loads each grammar with `Language.load`, compiles
highlight/injection queries, evaluates captures and predicates, remaps injected
node offsets, and emits SimpleHighlight tuples indexing the original JS/UTF-16
string. Query properties carry `conceal` and `conceal_lines` metadata.

Port the actual installed worker/binding behavior, including `#eq?`, matching,
property predicates, query settings, language aliases and injection mappings;
do not assume that every Neovim directive in a query is implemented by the source
worker. Preserve capture order, scope specificity/fallback, suppression of
`markup.raw.block` inside injected content, and conceal/source-line handling.
The new render contract retains injection-language and conceal metadata instead
of discarding it during transport.

The parser implementation must honor cancellation/progress checks and return
results tagged to the supplied revision. The rendering state rejects stale
results even if a producer finishes after cancellation. Do not enable a fake
"parser available" state before the grammar/query actually loaded.

## Alternatives and cost

1. **Recommended: Wasmtime + the pinned WASM/runtime/query assets.** Keeps the
   grammar binaries closest to upstream and avoids Node/Bun, but requires the
   Emscripten/web-tree-sitter host glue described above.
2. **Separate approval: native Tree-sitter C library plus native grammars per RID.**
   A simpler C ABI can reduce web-glue work, but this adds a different native
   parser deployment surface and cannot use the existing WASM grammars directly.
   Grammar/query versions and licensing still require pinning.
3. **Rejected for parity: heuristic or regex highlighting.** This does not provide
   Tree-sitter captures, injections, conceal behavior, or language coverage.

No option is adopted by this change. Approval is required before adding the
runtime/package/native parser engine.

## Pinned local assets inspected

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| web-tree-sitter 0.25.10 `tree-sitter.wasm` | 205488 | `f38dcc4b43b818f9a0785bc1c6d5611a75ac4cdd428ff3f02757c34ca4e46d7f` |
| JavaScript | 411770 | `5fb488d0cabb4775a594bab85682de5ad6ce83c0d6ac997a9f82dd084d571240` |
| TypeScript | 1413849 | `778025db5a8be0e70f8ccc3671e486dfeddd048c25d9e8a70c26de2e1bf6f97d` |
| Markdown | 421534 | `3e13182f21373634c40653f170e6f2d2790914eb2c243927086d79023c534f7a` |
| Markdown inline | 426020 | `9bbd71a70a23f6d0193bb162a72eecf2a2bd9ce76910d7ad3da9c5f80f122671` |
| Zig | 691726 | `54b3b83dd9c62da5815f06132bc3fc914d9dcc780370b32416446a0b7969e8c6` |

Query SHA-256 values from the same OpenTUI installation:

- JavaScript highlights: `c90e849891a3c8698992e10efdeaff7e7a8f98da47f941489566cb9e60f639b5`
- TypeScript highlights: `8e823819058d480c450ca4ef377a05675f6ed6f7c0460a69b972cdbe940c0893`
- Markdown highlights: `f3b02df1a9213cfecfb6936bce8db2f777edd523fc23ed890695e6cb4552d556`
- Markdown injections: `a2bf8c052454acbe765970a4ad2706c60cad7b62a762b927e28f60af1a0ef516`
- Markdown-inline highlights: `ca9a109ddd21c5ffdc6b84f00c0a4b2eeb55ae36a0b8eeb9b02348269c5acd22`
- Zig highlights: `f2232f0fde717543e4541cec871dd8633cb9bc6b5d1686a56b2d469c1dd9e6b6`

These binaries were only read as WASM sections and hashed. No module was loaded
into an engine, instantiated, or executed. Existing assets and queries were not
modified/copied into a new deployment.

## Language and license provenance

The bundled default descriptors cover JavaScript/javascriptreact,
TypeScript/typescriptreact, Markdown, Markdown inline, and Zig. The TUI's
`parsers-config.ts` adds separate remote descriptors: for example C# grammar
v0.23.1, Bash v0.25.0, Python v0.23.6, and others. Those additional binaries were
not present in the inspected bundled set and are not claimed supported here.
Some query URLs use floating `master`; freeze their exact content/commit/hash
before redistributing them rather than silently following the branch at runtime.

OpenTUI is MIT (copyright 2025 opentui); its license accompanies the projection
port. The inspected web-tree-sitter 0.25.10 LICENSE is MIT, copyright 2018–2024
Max Brunsfeld. Preserve that license and each grammar/query repository's specific
license and pinned revision when packaging assets. The OpenTUI aggregate license
must not be treated as proof that every downloaded query/grammar has been audited.

Validation is source/static metadata inspection and pinned .NET 11 builds only.
No parser/native/WASM execution, tests, or runtime probes were performed.
